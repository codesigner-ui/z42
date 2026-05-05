# Proposal: 引入 `ref` / `out` / `in` 参数修饰符（编译期验证）

## Why

z42 主语言体目前只有"按值传"和"引用类型自动按引用传"两种参数传递方式，未覆盖三类真实需求：多返回值人体工学（`TryParse` 模式）、修改调用方原语变量（`Increment(ref c)` / `Swap(ref a, ref b)`）、未来 user-defined struct 大值类型零拷贝传参。C# 在这块演化出 7+ 修饰符（`ref` / `out` / `in` / `ref readonly` / `scoped` / `ref struct` 等）互相弥补，是其复杂度的最大来源之一；z42 借鉴 C# 但以"单一形态 + 结构性约束"路线引入，避免修饰符链。

**本 spec 仅覆盖编译期验证（Lexer / Parser / TypeChecker / 编译期 DA）**。运行时实施（IR Codegen 端 ref-aware Call、VM `Value::Ref` + 透明 deref、跨 frame 索引基础设施）拆分到独立 spec `impl-ref-out-in-runtime`，与本 spec 的 design.md 决议保持一致（详见下文 Out of Scope）。

## What Changes

- **新增** 三个参数修饰符：`ref T x` / `out T x` / `in T x`（Lexer / Parser / AST 完整支持）
- **新增** callsite 强制语法：`f(ref x)` / `f(out var v)` / `f(in y)`，三者均强制写（修正 C# `in` 可省的不一致）
- **新增** `out` 参数 DefiniteAssignment：caller 端调用前可未初始化、调用后视为已赋值；callee 端必须在所有 normal-return 路径上对 `out` 参数赋值；throw 路径不要求赋值（与 C# 一致）
- **新增** modifier-based overload：`Foo(int)` 与 `Foo(ref int)` 视为不同重载（class methods + free functions 双侧 modifier-tagged key）
- **限制** 4 条：不可被 lambda 捕获 / 不可在 async 方法（占位拒绝）/ 不可在 iterator 方法（占位拒绝）/ generic `T` 不接受 ref/out/in 形态
- **限制** `in` 参数写保护：函数体内 `x = ...` 报错（callee 端只读契约）
- **更新** `language-overview.md` §5 旧 `out int result` 例子，改写为完整三修饰符小节 + tuple 多返回示例对照
- **更新** `interop.md` ABI 表新增 `ref T ↔ *mut T` / `out T ↔ *mut T` / `in T ↔ *const T` 映射
- **新建** `docs/design/parameter-modifiers.md`：完整规范 + "Deferred / Future Work" 段（D1-D6 + 运行时实施引用）
- **设计期延后** 6 项相关特性，写入 `parameter-modifiers.md` 的 "Deferred / Future Work" 段：D1 ref local / D2 ref return / D3 ref field / D4 ref struct / D5 scoped / D6 ref readonly
- **不引入** D1-D6 任何位置形态；维持 `mut` / `unsafe` 已有立场（feedback_no_mut_modifier / feedback_no_unsafe_keyword）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Syntax/Lexer/TokenKind.cs` | MODIFY | +`Ref` +`Out` token kinds（`In` 已存在，复用）|
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY | 注册 `"ref"` / `"out"` 关键字 |
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `Param` 加 `Modifier` 字段；新增 `ArgModifier` 枚举与 `ModifiedArg` / `OutVarDecl` 节点 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `ParseParamList` 支持前缀修饰符 |
| `src/compiler/z42.Syntax/Parser/ExprParser.cs` / `ExprParser.Atoms.cs` | MODIFY | callsite 修饰符 + `out var x` 内联声明 |
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | `BoundModifiedArg` + `BoundOutVarDecl` |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | `Z42FuncType.ParamModifiers` + `ModifierMangling` |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` / `SymbolCollector.Classes.cs` | MODIFY | modifier-aware overload key（class methods + free functions）|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` / `TypeChecker.Calls.cs` / `TypeChecker.Exprs.cs` / `TypeChecker.Exprs.Operators.cs` | MODIFY | 修饰符一致性 / lvalue / 严格类型 / DA / lambda 捕获禁止 / `in` 写保护 / overload 选择 |
| `src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs` | MODIFY | `out` DA caller post-call + callee normal-return（throw 路径除外）|
| `src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs` | MODIFY | `LookupParamModifier` 助手（lambda 捕获禁止 / `in` 写保护）|
| `src/compiler/z42.Semantics/TypeCheck/ITypeCheckPhase.cs` | MODIFY | `IFlowAnalyzer.CheckDefiniteAssignment` 加 functionParams 参数 |
| `src/compiler/z42.Tests/LexerTests.cs` | MODIFY | +3 token 测试 |
| `src/compiler/z42.Tests/ParserTests.cs` | MODIFY | +8 解析测试 |
| `src/compiler/z42.Tests/ParameterModifierTypeCheckTests.cs` | NEW | 20 个语义测试覆盖所有 scenarios |
| `docs/design/language-overview.md` | MODIFY | §5 重写函数小节 |
| `docs/design/parameter-modifiers.md` | NEW | 完整规范 + Deferred / Future Work 段（D1-D6 + 运行时引用）|
| `docs/design/interop.md` | MODIFY | ABI 表更新 |
| `docs/design/compiler-architecture.md` | MODIFY | modifier 流转原理 |
| `docs/roadmap.md` | MODIFY | Pipeline 进度表更新（仅编译期阶段）|

**只读引用**（理解上下文必读，不修改；不计入并行冲突）：
- `docs/design/closure.md` — lambda 捕获规则现状
- `docs/design/exceptions.md` — throw 路径对 DA 影响的先例
- `spec/archive/2026-04-29-impl-pinned-syntax/` — IR/VM 协调实施模式参考
- `src/compiler/z42.IR/IrModule.cs` — IR 数据模型
- `src/compiler/z42.Semantics/Codegen/*` — 当前 codegen 不在本 spec 改动范围

## Out of Scope

- **运行时实施**（IR Codegen ref-aware Call / VM `Value::Ref` + RefKind / 跨 frame 索引 / 透明 deref / `ref a[i]` / `ref obj.f` / 7 个 golden tests）—— 拆分到独立 spec `impl-ref-out-in-runtime`，与本 spec 的 design.md Decision 1/2/8/9 保持一致
  - 用户写 `Increment(ref c)` 在本 spec 阶段：编译期通过所有验证，运行时 codegen 走普通 by-value Call（callee 修改自己 param 不影响 caller）
  - 后续 spec 启动时无需再做编译期工作，只补 Codegen + VM
- **6 项设计期延后子特性**（在 `docs/design/parameter-modifiers.md` 的 "Deferred / Future Work" 段记录形态 + 延后理由 + 重启评估触发条件，本 spec 不实施）：
  - **D1**：`ref` 局部变量 `ref int x = ref expr`
  - **D2**：`ref` 返回类型 `ref T M()`
  - **D3**：`ref` 字段（绑定 `ref struct`，与 D4 同进退）
  - **D4**：`ref struct` 类型
  - **D5**：`scoped` 修饰符
  - **D6**：`ref readonly`（任何位置）
- async / iterator 与 ref 参数的真正集成 —— 本 spec 仅给"占位拒绝"诊断，等 async / iterator 引入时单独 spec
- `params` / `this`（扩展方法）—— 与本议题无关
- `readonly struct` 类型层不可变契约 —— 与 user-defined struct 提案一起决策

## Open Questions

无（编译期 scope 内的 Q1-Q8 决策已在 design.md 全部解决；运行时相关决策由后续 `impl-ref-out-in-runtime` spec 处理）。
