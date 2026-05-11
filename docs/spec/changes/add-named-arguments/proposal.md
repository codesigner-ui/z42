# Proposal: Named Arguments at Call Sites

## Why

z42 已支持默认参数（`void Greet(string name, string prefix = "Hello")`，[2026-04-04-add-default-params](../../archive/2026-04-04-add-default-params)），但调用方仍只能按位置传值。这带来两个痛点：

1. **可读性**：多参数调用如 `new Random(42, false, true, 100)` 不看签名读不出意图；C# / Kotlin / Swift 标配的 `Random(seed: 42, threadSafe: false, ...)` 形式 z42 缺位
2. **跳过中间默认参数**：当函数末尾两参数都有默认值，想覆盖第二个必须填第一个；named arg 是工业标准解决方式

`add-default-params` proposal 明确把 named args 列入 Out of Scope；本 spec 把它补齐。

## What Changes

### 核心新增

- **Parser**：识别 `<ident> : <expr>` 作为 named arg 形式；与 `ref/out/in` 前缀正交（`paramName: ref x` 合法）
- **AST**：`CallExpr.Args` 容器升级 — 每个 arg 可携带 optional `Name` 字段（保留 modifier）；新增/扩展 `Argument` 节点
- **TypeChecker**：
  - 按 param name 绑定到声明位置；overload candidate 必须同时匹配 arity + 名字集合
  - 错误码：unknown name / duplicate name / param 同时被 positional 和 named 指定 / required param 缺失
  - 位置规则：positional 必须在 named 之前（C# 风格）
- **Bound 层**：BoundCall 在 TypeCheck 解析后**已按 param 顺序持有 args**（向后兼容 — 下游 IrGen 不感知 named 概念）
- **IrGen**：基于 Bound 层已重排的 args 走 FillDefaults，跳过的中间位置走 default 表达式（已支持）

### 适用范围

- Function calls / method calls / virtual calls
- Constructor invocations (`new Random(seed: 42)`)
- Static method calls
- Native / extern 调用同样适用（IrGen 路径统一）

### 不修改

- **IR / zbc 二进制格式**：零改动（实参在 codegen 前已展开为位置形态）
- **VM / runtime**：零改动
- **delegate / lambda call**：z42 delegate 调用走 `FuncRef` indirect，没有静态参数名（lambda 参数名在定义处，调用方看不到）；与 C# 一致

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `Argument(string? Name, Expr Value, ArgModifier Modifier, Span)` record；`CallExpr.Args` 类型升级 `List<Expr>` → `List<Argument>` |
| `src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs` | MODIFY | `ParseCallArgWithOptionalModifier` lookahead `IDENT :`，识别 named arg；wrap 入 `Argument` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | event/delegate synthesized CallExpr 改 `List<Argument>` |
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | `BoundCall.Args` 保持 `List<BoundExpr>`（按 param 顺序）；增 `BoundCall.OriginalNamedIndices` 可选 debug 元数据 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` | MODIFY | named-arg 绑定算法（按 name → param 位置 → 填洞）；overload candidate 过滤 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | ObjNew (`new Foo(named: ...)`) 同路径 |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY | 新增 4 个错误码（unknown name / duplicate name / positional-after-named / param-doubly-specified）|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` | MODIFY | （可能仅 readonly）— BoundCall.Args 已按 param 顺序；FillDefaults 现有逻辑覆盖跳洞 |
| `src/compiler/z42.Tests/NamedArgumentsTests.cs` | NEW | Parser / TypeCheck / IrGen 单测套 |
| `src/tests/calls/named_args/` | NEW | golden e2e（含跳中间默认、ctor、ref/out 组合）|

**只读引用**：
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` — `Z42FuncType.Params` 已含 Name
- [docs/spec/archive/2026-04-04-add-default-params](../../archive/2026-04-04-add-default-params) — 立项前置
- `src/compiler/z42.IR/IrModule.cs` — 不动

**估计**：8 MODIFY + 2 NEW = 10 文件

## Out of Scope

- **新 IR 指令 / wire format 改动**：零（实参 codegen 期完全展开为位置形态）
- **lambda / delegate call 的 named args**：z42 与 C# 一致，indirect call 不支持 named（无静态参数名信息）
- **TestAttribute 的 `[Skip(reason: "x")]`**：已通过独立 `NamedArgs` 字典处理（attribute 解析路径独立），不在本 spec
- **named args 用于 generic type args**（`Foo<T: int>`）：不属于参数命名范畴
- **完整 C# parameter modifiers 组合**（`params` 参数末尾交互）：z42 `params` 当前形态稳定后再补
- **运行时反射读 param name**（`Std.Reflection.GetParameters`）：stdlib API 范畴，独立 spec

## Open Questions

- [ ] **CallExpr.Args 类型迁移策略**：直接换为 `List<Argument>`（侵入式，触 ~30 调用点），还是保留 `List<Expr>` + 平行加 `List<string?> Names`（最小侵入但状态分散）？倾向**侵入式**，单一真相来源。Decision 留 design.md
- [ ] **错误码编号**：现有 catalog 应该有可用区间（错误码风格 `Z0xxx`），具体编号在 design.md 选定
- [ ] **overload 解析时机**：先按 arity 过滤 → 按 name+types 精确匹配？还是 name 集合 hash 一步到位？倾向**两阶段**（与现有 overload 算法对齐）。Decision 留 design.md
- [ ] **out var 内联与 named 的组合**：`f(paramName: out var x)` 是否合法？倾向 **合法**（C# 7+ 允许），但 OutVarDecl 作用域规则要重新核对。Decision 留 design.md
