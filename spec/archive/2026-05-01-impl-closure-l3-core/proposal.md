# Proposal: 实现 L3 闭包核心 — 捕获 + 档 C 堆擦除 (impl-closure-l3-core)

## Why

`add-closures` 已定语言契约，`impl-lambda-l2` + `impl-local-fn-l2` 已落地无捕获子集。
现在 L2 边界的 `Z0301`（capture not enabled）卡住所有需要捕获的用例：
- stdlib HOF（Map / Filter / Reduce）需要捕获循环上下文 / 用户配置
- 事件处理 / async callback 都需要 closure 携带状态
- 用户写的简单 `var k = 10; var f = (int x) => x > k;` 直接编译错

不做会怎样：
- L3 阶段无法解锁，闭包设计只用了 30%
- stdlib 高阶 API 实现卡住
- 用户体验断层（C# 习惯写法报错）

本变更**端到端实现 L3 闭包的核心子集 — 捕获分析 + 档 C 堆擦除**，使下面这段代码全栈可跑：

```z42
void Main() {
    var k = 10;
    var pred = (int x) => x > k;          // 值类型捕获（快照）
    Assert.Equal(true, pred(15));

    k = 100;                              // 修改外层
    Assert.Equal(true, pred(15));         // 闭包仍用快照值 10

    var counter = new Counter();
    var inc = () => counter.n = counter.n + 1;   // 引用类型捕获（按身份）
    inc(); inc();
    Assert.Equal(2, counter.n);
}
```

档 A（栈分配优化）/ 档 B（单态化优化）/ JIT 路径补全 / 循环变量新绑定 拆到独立 follow-up 变更（C3-C6）。

## What Changes

### 实现的 Requirement（archived `add-closures` spec）

- **R5 值类型按快照捕获**：`var x = 5; var f = () => x; x = 10; f()` → 输出 5
- **R6 引用类型按身份共享**：闭包内外见同一对象
- **R12 编译器自动选实现**（部分）：本变更只实现**档 C 堆擦除**；档 A/B 留 follow-up
- **R8 spawn move + Send 派生** **暂缓**（user 决定，与 concurrency 一起做）—— spawn 闭包按普通规则捕获，**不做 Send 检查**

### 不实现（拆出）

- ❌ **R7 循环变量每次迭代新绑定** → `impl-closure-l3-loops` (C3)
- ❌ **JIT 路径补全**（LoadFn / CallIndirect / MkClos）→ `impl-closure-l3-jit-complete` (C4)
- ❌ **档 B 单态化**（性能优化）→ `impl-closure-l3-monomorphize` (C5)
- ❌ **档 A 栈分配 + 逃逸分析**（性能优化）→ `impl-closure-l3-escape-stack` (C6)
- ❌ **R14 `Ref<T>`** → **永久删除**（user 决议：z42 不引入 Rust 风格 `Ref<T>`，共享可变状态用 class，传引用用 C# `ref` 关键字独立提案）
- ❌ R10 闭包可比较 / R11 不可序列化 → 留到 stdlib 序列化提案处理
- ❌ R13 `--warn-closure-alloc` 诊断 → 拆到 C5/C6 性能优化阶段

### Closure 设计文档调整（在本变更内修订）

- `docs/design/closure.md` §4.4「共享可变值类型用 `Ref<T>` / `Box<T>`」**整章删除**，改写为说明"如需共享可变状态，使用 class（引用类型按身份共享）；C# `ref` 关键字是参数级特性，不可跨闭包"
- 决议表"共享可变值类型用 `Ref<T>` / `Box<T>`"对应行**删除**

### Pipeline 改动

- **AST/BoundExpr**：
  - `BoundLambda` 新增 `Captures: IReadOnlyList<BoundCapture>` 字段
  - 新增 `BoundCapture(string Name, Z42Type Type, BoundCaptureKind Kind, Span)` + `BoundCaptureKind { ValueSnapshot, ReferenceShare }`
  - 新增 `BoundCapturedIdent(string Name, Z42Type Type, int CaptureIndex, Span)` —— 替代 BoundIdent 当 ident 引用的是捕获项
- **TypeChecker**：
  - `_lambdaOuterStack` 改造为 `_lambdaBindingStack`，每帧含 `OuterEnv` + `Captures: List<BoundCapture>`
  - 进入 lambda body 前 push 新 frame；body bind 完 pop，得到 captures list
  - `BindIdent` 当名字解析到边界外的本地变量时，**不再报 Z0301**，改为：
    - 计算 `BoundCaptureKind`（由 `Z42Type.IsReferenceType` 判定）
    - append 到当前 frame 的 captures list
    - 返回 `BoundCapturedIdent` 替代 `BoundIdent`
  - L3 feature gate：lambda body 内访问外层 local **L3 阶段允许**（先按本变更默认开启；未来可加 LanguageFeature.L3Closure 控制）
- **Local function 同样路径**：local fn 体引用外层 local 时也走 capture 逻辑（沿用本机制）
- **IR**：
  - 新增 `MkClosInstr(TypedReg Dst, string FuncName, List<TypedReg> Captures)` IR 节点
  - 新增 Opcodes.MkClos = 0x57
  - ZbcReader/Writer 加序列化
  - IrVerifier 加验证
- **Codegen**：
  - `EmitLambdaLiteral` 路径分叉：
    - 无捕获 → 现有 `LoadFn` 路径（保持 lambda_l2_basic 行为）
    - 有捕获 → 新 `MkClos` 路径
  - 生成 lifted fn 时，**有捕获**的 lambda body 的第一个隐式参数为 env（类型 `IrType.Ref`，运行时是 `Value::Array`）
  - `BoundCapturedIdent` 在 IrGen 中 emit 为 `ArrayGetInstr(dst, env_reg=0, capture_index)`
- **VM**：
  - 新增 `Value::Closure { env: GcRef<Vec<Value>>, fn_name: String }` variant
  - `MkClos` 解释器：分配 env Vec<Value>，从 capture regs 拷入，构造 `Value::Closure`
  - `CallIndirect` 扩展：FuncRef 走原路径；Closure 时把 env 作为第一参 prepend，然后调用 fn_name 对应函数
- **测试**：parser 不变；TypeChecker / IrGen / VM 大量新测试 + golden

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/closure.md` | MODIFY | 删 §4.4 / 决议 #10；重写为 class 共享 + C# ref 独立 |
| `docs/roadmap.md` | MODIFY | L3-C 表 L2-C2 标 ✅（核心交付） |
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | 加 `BoundCapture` / `BoundCaptureKind` / `BoundCapturedIdent`；改 `BoundLambda` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | `_lambdaOuterStack` 重构为 `_lambdaBindingStack` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | `BindIdent` capture 路径；`BindLambda` 收集 Captures 并写入 BoundLambda |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs` | MODIFY | local fn 同样走 capture 路径（移除一层嵌套限制中的 capture 拒绝） |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `MkClosInstr` 节点 |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | `MkClos = 0x57` |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | `MkClos` 反序列化 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | `MkClos` 序列化 |
| `src/compiler/z42.IR/IrVerifier.cs` | MODIFY | `MkClos` 验证（fn 存在 + capture 数 ≥ 0）|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | `EmitLambdaLiteral` 分叉；`EmitLifted` 加 env param 处理；`BoundCapturedIdent` emit ArrayGet |
| `src/runtime/src/metadata/types.rs` | MODIFY | `Value::Closure { env, fn_name }` variant |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `Instruction::MkClos` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | OP_MK_CLOS 解码 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | `MkClos` 解释 + `CallIndirect` 处理 Closure |
| `src/runtime/src/jit/translate.rs` | MODIFY | `MkClos` bail（JIT 路径在 C4 补完）|
| `src/runtime/src/corelib/convert.rs` | MODIFY | `Value::Closure` 的 ToString |
| `src/runtime/src/gc/rc_heap.rs` | MODIFY | `Value::Closure` 的 size + scan |
| `src/compiler/z42.Tests/ClosureCaptureTypeCheckTests.cs` | NEW | 捕获分析单元测试 |
| `src/compiler/z42.Tests/ClosureCaptureIrGenTests.cs` | NEW | IR snapshot tests |
| `src/runtime/tests/golden/run/closure_l3_capture/` | NEW | 端到端 golden（值类型 + 引用类型 + nested + local fn 捕获）|
| `examples/closure_capture.z42` + `.toml` | NEW | 示例 |
| `spec/changes/impl-closure-l3-core/{proposal,design,tasks}.md` + `specs/...` | NEW | 本变更规范 |

**只读引用**：
- `spec/archive/2026-05-01-add-closures/specs/closure/spec.md` — R5/R6 行为契约
- `spec/archive/2026-05-01-impl-lambda-l2/` — LoadFn / CallIndirect 已就位
- `spec/archive/2026-05-01-impl-local-fn-l2/` — 同样 capture 路径

## Out of Scope

- ❌ **Send 派生 / spawn move 强制**（user 决定，与 concurrency 一起做）—— spawn 内 lambda 按本变更普通规则捕获
- ❌ **R7 循环变量新绑定** → `impl-closure-l3-loops`
- ❌ **JIT 路径** LoadFn / CallIndirect / MkClos → `impl-closure-l3-jit-complete`（lambda golden 仍 interp_only）
- ❌ **档 B 单态化** → `impl-closure-l3-monomorphize`
- ❌ **档 A 栈分配 + 逃逸分析** → `impl-closure-l3-escape-stack`
- ❌ **R10 / R11 闭包比较 / 序列化** → 序列化 stdlib 提案
- ❌ **R13 `--warn-closure-alloc`** → 与 C5/C6 同期
- ❌ **R14 `Ref<T>` 类型** → **永久删除**（用 class 替代）
- ❌ **C# `ref` 关键字** → 独立提案 `add-ref-params`，不在 L3 闭包序列内

## Open Questions

- [ ] L3 capture 解锁是否需要 `LanguageFeature.L3Closure` 显式 gate？建议**不需要**——直接默认开启，本变更归档时 codebase 即支持
- [ ] 同名嵌套 capture：lambda 内 lambda 都引用最外层 `k`，内层 lambda 的 capture 是从外层 lambda 的 env 取，还是直接从最外层取？建议**直接从最外层取**——内层 lambda 把 `k` 也加入自己的 captures list（生成代码时各自 env 拷贝一份）
- [ ] env 类型是用 `Value::Array` 还是合成 ScriptObject？建议 **Value::Array**——最简单，GC 直接复用现有 Array 路径，slot 索引而非字段名（debug info 后续补）
- [ ] Local function 捕获时是 lambda 还是普通 function？current `impl-local-fn-l2` 一层嵌套限制中的 "capture 拒绝" 改为 "capture 允许 + 走 MkClos"——需要检查 BindLocalFunctionStmt 是否一致
