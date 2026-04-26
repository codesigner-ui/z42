# Tasks: ObjNew ctor name dispatch

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：ir (zbc 版本 bump 0.6 → 0.7)

## 进度概览
- [x] 阶段 0: 调研验证 — ctor 命名约定（单 = no suffix；多 = `$N`）+ JIT 路径在 helpers_object.rs:164 + Methods dict keys 不含 `Class.` prefix
- [x] 阶段 1: C# IR + Bound 数据结构（ObjNewInstr / BoundNew 加 CtorName）
- [x] 阶段 2: TypeChecker ResolveCtorName（含 default params 支持 + base ctor overload resolution）
- [x] 阶段 3: Codegen + ZbcWriter / ZbcReader / ZasmWriter
- [x] 阶段 4: Rust VM + zbc loader（无兼容路径，按 workflow 不为旧版本兼容）
- [x] 阶段 5: zbc 版本 bump 0.6 → 0.7（C# + Rust 一致）
- [x] 阶段 6: 测试 + 回归 + 重生 91 source.zbc 为 0.7 格式
- [x] 阶段 7: 文档同步 + 归档

---

## 阶段 0: 调研验证

- [ ] 0.1 Dump z42.core.zpkg 确认 ctor 函数命名约定（单 ctor 是否真无 `$N` /
  双 ctor 是 `$1`/`$2`）—— Wave 2 前我们看过类似 disasm，再确认
- [ ] 0.2 grep `Instruction::ObjNew` / `OP_OBJ_NEW` / `ObjNewInstr` 全部出现处
  确认要改的代码点
- [ ] 0.3 验证 ImportedSymbolLoader 加载 ClassType.Methods 字典 keys 形式
  （是 `Class.simple` / `Class.simple$N` 还是仅 `simple` / `simple$N`）—
  影响 ResolveCtorName 写法

## 阶段 1: C# IR + Bound 数据结构

- [ ] 1.1 `IrModule.cs ObjNewInstr` 加 `string CtorName` 字段
- [ ] 1.2 `IrVerifier.cs` 检查（若涉及 ClassName，对应处理 CtorName）
- [ ] 1.3 `BoundExpr.cs BoundNew` 加 `string CtorName` 字段

## 阶段 2: TypeChecker ctor overload resolution

- [ ] 2.1 在 `TypeChecker.Exprs.cs NewExpr` 处理中加 `ResolveCtorName(qualName, args.Count)`
  辅助函数；遵循 design Decision 2 算法
- [ ] 2.2 `BoundNew(qualName, args, ctorName, newType, span)` 构造
- [ ] 2.3 单元测试：单 ctor 命名 + 双 ctor 按 arity 选 + 类未找到 fallback

## 阶段 3: Codegen + zbc 编/解码 + Zasm

- [ ] 3.1 `FunctionEmitterExprs.EmitBoundNew` 用 `n.CtorName` 替换硬编码
  ctorKey 推导；emit `ObjNewInstr(dst, qualCls, n.CtorName, argRegs)`
- [ ] 3.2 `ZbcWriter.Instructions.cs` ObjNewInstr 编码加 ctor_name pool idx；
  pool intern 加 CtorName
- [ ] 3.3 `ZbcReader.Instructions.cs` 解码：直接读 ctor_name pool idx（无兼容分支）
- [ ] 3.4 `ZasmWriter.cs` 显示 CtorName（与 default 不同时附 `ctor=...`）
- [ ] 3.5 `ZbcRoundTripTests` 添加 ObjNew + CtorName 用例

## 阶段 4: Rust VM + zbc loader

- [ ] 4.1 `bytecode.rs Instruction::ObjNew` 加 `ctor_name: String` 字段
- [ ] 4.2 `binary.rs OP_OBJ_NEW` 解码 ctor_name pool idx（无兼容分支）
- [ ] 4.3 `zbc_reader.rs OP_OBJ_NEW` 同上
- [ ] 4.4 `interp/exec_instr.rs ObjNew`：用 ctor_name 直查；删除
  `${class}.${simple}` 推断
- [ ] 4.5 JIT 端 ObjNew 路径同步（具体位置阶段 0 调研确定；可能在
  `jit/translate.rs` 或 helpers）

## 阶段 5: zbc 版本 bump 0.4 → 0.5

- [ ] 5.1 `formats.rs ZBC_VERSION` 从 `[0, 4]` → `[0, 5]`
- [ ] 5.2 `ZbcFile.cs` 中 `CurrentVersion` 同步（看 C# 侧版本常量位置）
- [ ] 5.3 `docs/design/ir.md` 记录 0.5 changelog（OP_OBJ_NEW 加 ctor_name）

## 阶段 6: 测试 + 回归

- [ ] 6.1 单元测试（阶段 2.3 和 3.5 中包含）
- [ ] 6.2 新增 `run/96_ctor_overload` golden — 用户类双 ctor 构造
- [ ] 6.3 `./scripts/regen-golden-tests.sh` 重生所有 source.zbc 为 0.5 格式
- [ ] 6.4 GREEN：dotnet build / cargo build / dotnet test / test-vm.sh /
  cargo test 全部全绿
- [ ] 6.5 （已 obsolete — 不留兼容路径，全部重生为 0.5 即可）

## 阶段 7: 文档同步 + 归档

- [ ] 7.1 `docs/design/ir.md`：ObjNewInstr 新签名；zbc 版本 0.5 changelog
- [ ] 7.2 `docs/design/vm-architecture.md`：ObjNew dispatch 章节更新
  （与 VCall 对齐 — 编译期 resolve，VM 直查）
- [ ] 7.3 tasks.md 状态 → `🟢 已完成`
- [ ] 7.4 归档 + commit + push（scope `feat(ir+vm+typecheck)`）

## 备注

实施变更：

- **base ctor 也需要 overload resolution**（spec 未列出，实施发现）：
  `FunctionEmitter.cs:70` 的 `baseCtorIrName` 硬编码 `${baseQual}.${baseSimple}`
  无 suffix。当 base class 有重载 ctor（如 Wave 2 后插的 Exception 双 ctor），
  子类 `: base(message)` 会调用 `Std.Exception.Exception` 找不到。
  修复：新增 `ResolveBaseCtorKey` helper（与 ResolveCtorName 同算法）。
- **arity 含 default params**：`Logger(string prefix = "INFO")` 允许 0 args
  调用，需用 `MinArgCount..Params.Count` 闭区间匹配。
- **Z42FuncType.Params 不含 this**：算法是 `Params.Count == argCount`（无 +1）。
- **zbc 版本号两端不一致历史**：C# 在 0.6（`L3-G2.5 bare-typeparam: constraint
  bundle adds type_param_constraint` 注释），Rust 还停在 0.4。本变更同步到
  0.7（C# 0.6 + 1，Rust 0.4 + 3 跳步追平）。
- **Wave 2 后插**：恢复 Exception 双 ctor + 91 测试加回 InnerException chain
  作为 #2 端到端验证。新增 `run/96_ctor_overload`（用户类三 ctor）。
