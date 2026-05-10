# Proposal: D1a — `delegate` 关键字 + 命名 delegate 类型（含泛型）+ 单播 Invoke

> 这是 `docs/design/delegates-events.md` D1 阶段的语言层切片（D1a）。
> 配套子 spec：D1b 方法组转换 + 缓存；D1c stdlib 真实类型 + 移除 hardcoded desugar。
>
> **2026-05-02 scope 调整（user 裁决）**：
> - 嵌套 delegate（class body 内声明）**支持**
> - 泛型 delegate（`delegate R Func<T,R>(T arg)`）+ where 约束 **支持**
> - `delegate` 关键字未来要兼容 C# 风的 `delegate*<T,R>` unmanaged func pointer 语法 → Parser 设计预留

## Why

`docs/design/delegates-events.md` §3 规定单播 callable 类型为 `delegate` 关键字声明，但 z42 当前：

- **无 `delegate` 关键字**（TokenKind / TokenDefs 都没有；当前用关键字声明 callable 不可能）
- **无 `DelegateDecl` AST 节点**（`CompilationUnit` 只有 Classes/Functions/Enums/Interfaces）
- **`Action<>` / `Func<>` 在 SymbolCollector 写死 desugar 为 `Z42FuncType`** —— 不是真实类型，无法 `delegate` 自定义命名（用户写不了 `delegate void OnClick(MouseArgs e);`）

后果：
- 用户无法表达"命名 callable 类型"——只能用 `(T) -> R` 字面量类型，每个 site 重复
- 一切 callable 字段都得用 `(T) -> R` 字面量，**事件、回调、约定接口签名缺失共享名字**
- 与 C# 视觉无法对齐（`event Action<MouseArgs> Click;` 这种代码无法直接迁移）
- D1b 的方法组转换、D2 的 event / multicast 都依赖"命名 delegate 类型"先行存在

## What Changes

**D1a 语言层完整支持**：

- **Lexer**：新增 `Delegate` TokenKind + `TokenDefs.cs` `delegate` 关键字注册
- **Parser**：
  - 新增 `DelegateDecl` sealed record（含 `TypeParams` + `WhereClause` 字段，与 `ClassDecl` / `FunctionDecl` 风格一致）
  - `TopLevelParser.Types.cs::ParseDelegateDecl()` 解析：`<TypeParams>?` + `(Params)` + `where`-clause？
  - **类内嵌套**：`ParseClassDecl` 成员循环识别 `delegate` token，调相同 `ParseDelegateDecl`，结果挂到 ClassDecl（具体如何承载 — 见 design.md Decision 3）
  - **顶层** + **嵌套** 两条路径产物 shape 一致
  - 预留未来 `delegate* <T,R>` unmanaged 语法：`Delegate` token 后第一个非空 token 若是 `*` 直接抛 `not yet supported`，避免与正常 delegate 解析路径冲突
- **TypeChecker**：
  - `SymbolTable.Delegates` 跟踪所有 delegate（顶层 + 嵌套），key 含必要 qualification（class name / arity）
  - `DelegateInfo(Z42FuncType Signature, IReadOnlyList<string> TypeParams, GenericConstraintBundle? Constraints)`
  - `ResolveType` `NamedType` 命中 delegate 名 → 返回 `Z42FuncType`；`GenericType` 命中 → 实例化（substitute type params）
  - 泛型约束验证复用 `ValidateGenericConstraints` 现有路径
- **同名多 arity**：`Action<T>` vs `Action<T1,T2>` 必须共存（design.md Decision 7 给出方案）
- **Codegen**：**零变更** —— delegate 类型 = `Z42FuncType`，IR 层走现有 LoadFn / MkClos / CallIndirect 路径
- **VM**：**零变更** —— `Value::FuncRef` / `Value::Closure` / `Value::StackClosure` 已就绪
- **Invoke 调用语法**：`d(args)` 走现有 BindCall var-of-FuncType 分支；`d.Invoke(args)` 显式方法语法留后续视需要补
- **错误处理**：null delegate 调用 → 抛 `NullReferenceException`（与 closure CallIndirect 现有行为一致）

> D1a **暂不**实现：方法组转换缓存（D1b）/ stdlib 真实 `Action`/`Func`/`Predicate` 类（D1c）/ `delegate ==` 等价比较 / 协变逆变（推迟）/ D2 多播 + event 关键字

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Lexer/TokenKind.cs` | MODIFY | 增加 `Delegate` |
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY | `KeywordDefs` 中加 `("delegate", TokenKind.Delegate, Phase1)` |
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `DelegateDecl` sealed record（含 TypeParams + Where + 可选 ContainerClass 用于嵌套）；CU 加 `List<DelegateDecl> Delegates` 持顶层 delegate；`ClassDecl` 加 `List<DelegateDecl> NestedDelegates` 持嵌套 delegate |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | 新增 `ParseDelegateDecl()` 解析头部 + TypeParams + Params + Where；`ParseClassDecl` 成员循环识别 `Delegate` token 分支 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | MODIFY | 顶层循环识别 `TokenKind.Delegate` 分支 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs` | MODIFY | 新增 `Delegates: IReadOnlyDictionary<string, DelegateInfo>` 字段（key = qualified name + arity；详见 design.md Decision 7）；`DelegateInfo(Signature, TypeParams, ConstraintBundle?)` |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` | MODIFY | 收集顶层 + 嵌套 delegate；ResolveType 的 NamedType / GenericType 路径优先查 delegate 表 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.GenericResolve.cs` | MODIFY | 新增 delegate 的 where-clause 解析路径（与 class / func 同款） |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | Infer 时把 SymbolTable.Delegates 暴露给 SemanticModel（如果 IrGen 需要）|
| `src/compiler/z42.IR/IrModule.cs` | NO CHANGE | 复用现有 LoadFn / MkClos / CallIndirect |
| `src/runtime/src/...` | NO CHANGE | 复用 Value / FuncRef / Closure |
| `examples/` | NEW | `examples/delegate_basic.z42` —— 演示 delegate 声明 + 实例化 + 调用 |
| `src/compiler/z42.Tests/DelegateDeclParserTests.cs` | NEW | 解析单元测试 |
| `src/compiler/z42.Tests/DelegateDeclTypeCheckTests.cs` | NEW | TypeCheck 单元测试 |
| `src/runtime/tests/golden/run/delegate_d1a/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/delegate_d1a/expected_output.txt` | NEW | golden 期望输出 |
| `src/runtime/tests/golden/run/delegate_d1a/source.zbc` | NEW | regen 产物 |
| `docs/design/closure.md` | MODIFY | §3.2 修正：从 "**不**使用 Func/Action" 改为 "delegate 关键字+ named delegate 类型，详见 delegates-events.md" |
| `docs/design/delegates-events.md` | MODIFY | 顶部状态从 "前瞻性设计草案" 改为 "D1a 已落地（2026-05-02）"；§11.2 D1 行加完成标记 |
| `docs/roadmap.md` | MODIFY | 已完成关键 fix 表新增一行 |

**只读引用**：

- `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs::ParseEnumDecl` —— 头部签名 + 单次定义参考
- `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs:248-253` —— 现有 `Action`/`Func` desugar 路径（D1c 移除）
- `docs/design/delegates-events.md` §3 —— 单播 delegate 设计

## Out of Scope

- ❌ 方法组转换 + I12 缓存（D1b）
- ❌ stdlib 真实 `Action` / `Func` / `Predicate` delegate 类型 + N arity（D1c）
- ❌ 删除 `SymbolCollector` 中现有 `Action`/`Func` 硬编码 desugar（D1c 一并清理）
- ❌ event 关键字 / 多播 / ISubscription（D2）
- ❌ `delegate.Invoke()` 显式方法语法（v1 仅支持 `d(args)` 调用）
- ❌ `delegate ==` 比较 / `delegate.Method` / `delegate.Target` 等反射 API（推迟到 L3-R）
- ❌ 协变 / 逆变（`<in T, out R>`）（delegates-events.md §12 已明确推迟）
- ❌ `delegate*<T,R>` unmanaged func pointer 语法（D1a Parser 仅预留 token，遇到 `*` 报"not yet supported"）

## Open Questions

- [ ] **嵌套 delegate 的命名空间 / qualification 规则**：class A 内的 `delegate void Inner();` 在外部如何引用？倾向 `A.Inner`（与 nested class / enum 一致；如果 z42 现有不支持嵌套类型则停下讨论）
- [ ] **同名多 arity 的 SymbolTable key**：design.md Decision 7 选 `name+arity`（"Action$1" / "Action$2"）；与现有 generic class 重载（`f$2`）风格一致 —— **实施时验证**
- [ ] **Visibility**：delegate 支持 `public` / `internal` / `private` 与现有 class / enum 一致；具体 default 待验证
- [ ] **`delegate*<T,R>` unmanaged func pointer**：v1 不支持但 token 层应预留；Parser 遇 `delegate *` 报清晰错误，避免后续添加时 grammar 兼容压力
