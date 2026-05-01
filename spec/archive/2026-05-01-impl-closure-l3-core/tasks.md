# Tasks: 实现 L3 闭包核心 (impl-closure-l3-core)

> 状态：🟢 已完成 | 创建：2026-05-01 | 归档：2026-05-01 | 类型：lang（实施变更）

## Scope 偏离记录（实施期间）

实施过程中四处与 spec 偏离，归档前如实记录：

1. **closure.md 决议表**: 设计文档没有列编号决议表（决议表在 archived
   `add-closures` proposal.md 中），所以 task 1.2/1.3 不适用 —— 仅 §4.4 重写
   生效。决议 #10 的"删除"靠 `add-closures` archive 中的状态，但 archive
   不可改；这里在 closure.md §4.4 显式说明"不引入 Ref<T>"足矣。
2. **`_lambdaOuterStack` → `_lambdaBindingStack`**: 完成。还增加了 `Captures` /
   `NameToIndex` 字段以收集 capture（不只是 outer env 引用）。
3. **嵌套 capture 透传 bug**: 实施 golden test 时发现 g 在 f 内时，仅 g 自己
   收集 capture，f 没有 → f 的 lifted body 无法构造 g 的 env。修复：BindIdent
   遍历 `_lambdaBindingStack` 自底向顶，每个 boundary 之外的帧都补充 capture。
4. **Capturing local fn 的 call site**: 实施 golden 时发现 `Helper(x)` 在
   capturing 后变为闭包但 BindCall 仍走静态名字路径。修复：FunctionEmitterCalls
   `EmitBoundCall.Free` 加分支：name 在 _locals（且不在 lifted 名表）→
   CallIndirect on 该 reg。
5. **Pre-existing field default not initialized**: 测试时发现 `class C
   { public int n = 0; }` 的 default 0 不自动初始化（z42 既有行为，与本
   变更无关）。golden 改为显式 ctor `Counter() { this.n = 0; }` 绕过。
   作为 follow-up 单独修复（不阻塞本变更）。

## 进度概览
- [x] 阶段 1: closure.md 设计文档调整（删 R14 / §4.4 / 决议 #10）
- [x] 阶段 2: BoundExpr 节点（BoundCapture / BoundCapturedIdent / Captures 字段）
- [x] 阶段 3: TypeChecker（lambda binding 栈重构 + capture 收集）
- [x] 阶段 4: IR（MkClos opcode + 序列化 + Verifier）
- [x] 阶段 5: Codegen（EmitLambdaLiteral 分叉 + EmitLiftedWithEnv + BoundCapturedIdent → ArrayGet）
- [x] 阶段 6: VM（Value::Closure variant + MkClos interp + CallIndirect dispatch）
- [x] 阶段 7: 测试体系（TC unit + IrGen unit + Golden + Examples）
- [x] 阶段 8: GREEN + 归档

## 阶段 1: closure.md 设计文档调整

- [x] 1.1 `docs/design/closure.md` §4.4「共享可变值类型」整章重写
  - 删 `Ref<T>` / `Box<T>` 用法
  - 改为说明 "用 class 共享可变状态（引用类型按身份）；C# `ref` 是参数级独立特性"
- [x] 1.2 决议表第 10 条删除（"共享可变值类型用 Ref<T>/Box<T>"）
- [x] 1.3 §6 决议总数同步调整（15 → 14）

## 阶段 2: BoundExpr 节点

- [x] 2.1 `Bound/BoundExpr.cs`：新增 `BoundCaptureKind { ValueSnapshot, ReferenceShare }` enum
- [x] 2.2 新增 `BoundCapture(string Name, Z42Type Type, BoundCaptureKind Kind, Span Span)` record
- [x] 2.3 新增 `BoundCapturedIdent(string Name, Z42Type Type, int CaptureIndex, Span Span) : BoundExpr`
- [x] 2.4 改造 `BoundLambda` 加 `Captures: IReadOnlyList<BoundCapture>` 字段
- [x] 2.5 全 codebase 搜 `BoundLambda(` 创建点适配新签名

## 阶段 3: TypeChecker

- [x] 3.1 `TypeChecker.cs`：定义私有 `LambdaBindingFrame { OuterEnv; Captures; NameToIndex }`
- [x] 3.2 把 `_lambdaOuterStack: Stack<TypeEnv>` 替换为 `_lambdaBindingStack: Stack<LambdaBindingFrame>`
- [x] 3.3 `BindLambda` push frame；body bind 完 pop；Captures 写入 BoundLambda
- [x] 3.4 `BindIdent` capture 路径：边界外 → 记录 capture + 返回 BoundCapturedIdent（不报 Z0301）
- [x] 3.5 `BindLocalFunctionStmt` 适配新 stack（local fn 进入也 push frame）
- [x] 3.6 验证：所有现有 lambda / local fn 测试仍通过（840 + golden）

## 阶段 4: IR

- [x] 4.1 `IrModule.cs`：新增 `MkClosInstr(Dst, FuncName, Captures: List<TypedReg>)` record
- [x] 4.2 `Opcodes.cs`：`MkClos = 0x57`
- [x] 4.3 `ZbcWriter.Instructions.cs`：MkClos 序列化（含 InternInstrStrings + VisitInstrRegs）
- [x] 4.4 `ZbcReader.Instructions.cs`：`OP_MK_CLOS = 0x57` 反序列化
- [x] 4.5 `IrVerifier.cs`：MkClos 加入 GetDef + GetUses

## 阶段 5: Codegen

- [x] 5.1 `FunctionEmitterExprs.cs`：`EmitLambdaLiteral` 分叉
  - 无捕获 → 现有 LoadFn 路径
  - 有捕获 → MkClos 路径
- [x] 5.2 实现 `EmitCaptureExpr(BoundCapture)`：返回当前 emitter 中 capture 名字对应的 reg
- [x] 5.3 实现 `EmitLiftedWithEnv(name, BoundLambda)`：lifted body 第 0 reg 为 env，用户参数从 reg 1 开始
- [x] 5.4 `EmitExpr` 加 `case BoundCapturedIdent` → `ArrayGetInstr(dst, env_reg=0, const_idx)`
- [x] 5.5 Local function 路径同步：BoundLocalFunction 含 Captures 时走 MkClos + lifted env param

## 阶段 6: VM

- [x] 6.1 `runtime/src/metadata/types.rs`：`Value::Closure { env: GcRef<Vec<Value>>, fn_name: String }` variant
- [x] 6.2 `runtime/src/metadata/bytecode.rs`：`Instruction::MkClos { dst, fn_name, captures: Vec<Reg> }`
- [x] 6.3 `runtime/src/metadata/zbc_reader.rs`：`OP_MK_CLOS = 0x57` + 解码
- [x] 6.4 `runtime/src/interp/exec_instr.rs`：MkClos 解释（heap.alloc_array + Value::Closure）
- [x] 6.5 同上：CallIndirect 扩展 — 模式匹配 FuncRef vs Closure，Closure 时 prepend env
- [x] 6.6 `runtime/src/jit/translate.rs`：MkClos bail "L3+ closure JIT" + extend max_reg dst tracking
- [x] 6.7 `runtime/src/corelib/convert.rs`：value_to_str 加 `Value::Closure` arm（`<closure {fn_name}>`）
- [x] 6.8 `runtime/src/gc/rc_heap.rs`：object_size_bytes / scan_object_refs 加 Closure 处理

## 阶段 7: 测试体系

### 7.1 TypeChecker unit tests
- [x] 7.1.1 `ClosureCaptureTypeCheckTests.cs` NEW
  - L3-C-1 各种 capture kind（值类型 / 引用类型 / 多次引用 dedup / 不捕获自身参数）
  - L3-C-9 同名嵌套捕获
  - L3-C-10 local fn 捕获
  - L3-C-11 spawn 不强制 Send（暂缓）

### 7.2 IrGen snapshot tests
- [x] 7.2.1 `ClosureCaptureIrGenTests.cs` NEW
  - L3-C-2 无捕获 → LoadFn / 有捕获 → MkClos
  - L3-C-7 lifted body env param 出现
  - BoundCapturedIdent → ArrayGet snapshot

### 7.3 Golden tests
- [x] 7.3.1 `closure_l3_capture/value_snapshot/` 端到端值类型快照
- [x] 7.3.2 `closure_l3_capture/ref_share/` 引用类型身份共享
- [x] 7.3.3 `closure_l3_capture/nested_capture/` 同名嵌套
- [x] 7.3.4 `closure_l3_capture/local_fn_capture/` local fn 捕获
- [x] 7.3.5 各 golden 加 `interp_only` 标记
- [x] 7.3.6 `regen-golden-tests.sh` + `test-vm.sh interp` 全绿

### 7.4 Examples
- [x] 7.4.1 `examples/closure_capture.z42` 综合示例
- [x] 7.4.2 `examples/closure_capture.z42.toml`

## 阶段 8: GREEN + 归档

- [x] 8.1 `dotnet build` / `cargo build`：0 错误 0 警告
- [x] 8.2 `dotnet test`：100% 通过（含新增 ~20 测试）
- [x] 8.3 `./scripts/test-vm.sh`：100% 通过（lambda_l2_basic + local_fn_l2_basic + closure_l3_capture 同 interp_only）
- [x] 8.4 `docs/roadmap.md` L3-C 表 L2-C2 标 ✅
- [x] 8.5 移到 `spec/archive/2026-05-01-impl-closure-l3-core/` + commit + push

## 备注

实施过程中需要特别留意：
- **重构 `_lambdaOuterStack` 影响范围**：lambda + local fn 都用此栈做无捕获检查；改造时确保两者新行为一致（lambda 允许 capture，local fn 也允许）
- **`Value::Closure` 加入是 enum non-exhaustive**：Rust 编译期会指出所有需要更新的 match —— 按编译错误逐个修
- **嵌套 lambda 的 capture 路径递归性**：g 在 f 内创建时，g 的 capture_regs 通过 f 的 env ArrayGet 取得；测试时构造 `var k=10; var f = () => { var g = () => k; return g(); }` 验证
- **零 capture lambda 必须保留 LoadFn**：避免 lambda_l2_basic 回归
