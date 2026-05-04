# Proposal: 嵌套 delegate 外部 dotted-path 访问（D-6）

## Why

D1a（`add-delegate-type`，2026-05-02）落地嵌套 delegate 时**已经**把 qualified key 写入符号表（`SymbolCollector.cs:121-134`：`Btn.OnClick` / `Btn.OnClick$N` 都注册了），但 `ResolveType(NamedType)` 路径 **不消费 qualified key** —— 外部代码写 `Btn.OnClick handler;` 类型解析失败。

不做的痛点：
- 用户被迫把所有要在外部引用的 delegate 顶层声明，破坏 "声明在使用方" 的封装习惯
- C# 方风格的 `EventHandler` 类内嵌写法在 z42 不可用
- D-7 单播 event 完整后，`event SomeOuter.NestedDelegate Foo` 这种类型表达式无法工作

scope 小但价值高：qualified key 已在符号表，缺的只是 type 表达式 + lookup 的 dotted-path 协议。

## What Changes

1. AST 加 `MemberType(Left: TypeExpr, Right: string, Span)` 节点（代替 `NamedType` 表达 `Outer.Inner`）
2. TypeParser 在解析 NamedType 后看是否跟 `.` + 标识符；若是，包成 MemberType（递归直到没有 `.`）
3. `SymbolTable.ResolveType` 加 `MemberType` 分支：左 resolve 到 class/struct → 在该类的 nested delegates 里查右名 → hit qualified key → 返回 signature
4. （可选）若左 resolve 到非 class（如 namespace 或当前未支持的别名），返回友好错误而非 silent miss
5. 测试：嵌套 delegate 在外部以 `Owner.NestedName` 形式被声明 / 用作字段 / 用作参数 / 用作返回类型

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 加 `sealed record MemberType(TypeExpr Left, string Right, Span Span) : TypeExpr` |
| `src/compiler/z42.Syntax/Parser/TypeParser.cs` | MODIFY | NamedType 解析后 lookahead `.` + Ident，循环扩成 MemberType 链；保持 GenericType 兼容（`Outer.Inner<T>` 也合法） |
| `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs` | MODIFY | `ResolveType(MemberType mt)` 分支：resolve Left 拿 owner class → 用 `Right` 查 owner 的 NestedDelegates qualified key |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` 或对应 schema | MODIFY | 若 ClassType 当前没暴露 NestedDelegates 字段供查询，加只读访问器（已有内部数据，复核 schema） |
| `src/compiler/z42.Tests/Parser/TypeParserTests.cs`（或 NestedDelegateTests 新建） | NEW/MODIFY | parser 单测：`Btn.OnClick` 解析为 MemberType；`Btn.OnClick<T>` 解析为带泛型的 MemberType |
| `src/compiler/z42.Tests/Semantics/NestedDelegateAccessTests.cs` | NEW | TypeChecker 单测：① 外部用 `Btn.OnClick` 作为字段类型 ② 作为参数 ③ 作为返回类型 ④ 不存在的 nested name 报 E0401 |
| `src/runtime/tests/golden/run/nested_delegate_dotted/source.z42` | NEW | golden：跨类引用 nested delegate 编译 + 运行 |
| `src/runtime/tests/golden/run/nested_delegate_dotted/expected_output.txt` | NEW | golden 期望输出 |
| `docs/design/delegates-events.md` | MODIFY | 嵌套 delegate 章节加 dotted-path 落地说明（Open Question 1 标已答） |
| `docs/design/language-overview.md` | MODIFY | 类型表达式语法段加 MemberType（Outer.Inner）说明 |
| `docs/deferred.md` | MODIFY | 移除 D-6 条目 |

**只读引用**：
- `spec/archive/2026-05-02-add-delegate-type/` D1a Open Question 1
- `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs:92-137` qualified key 注册逻辑

## Out of Scope

- **嵌套 class / struct / enum**：z42 当前只允许 delegate 嵌套（per D1a Decision 3）。本变更只解决 delegate 的 dotted-path，不开放其他嵌套类型
- **任意深层嵌套（>2 层）**：当前 nested delegate 只在 class 一层内，不支持 `Outer.Inner.DeeperDelegate`。MemberType 递归实现支持，但 SymbolCollector 只注册一层 qualified key
- **Namespace dotted-path**：`Std.IO.MyDelegate` 已经走 namespace resolution（不是 nested-type）；本变更不动这条路径
- **跨 zpkg 嵌套 delegate**：deferred 描述只说类内嵌引用；跨 zpkg 复杂度（需要 ImportedSymbols 跨 module 暴露 NestedDelegates）放后续

## Open Questions

- [x] **`Outer.Inner<T>` 泛型嵌套**：MemberType 是否需要支持 `<...>` suffix？z42 当前 generic delegate 都在 top-level；nested generic delegate 用例边缘 → 本变更**支持解析**（parser 接受），但 SymbolCollector / ResolveType 现状只 lookup 非泛型嵌套。若 lookup 失败给清晰错误："nested generic delegates not supported (declare at top level)"
