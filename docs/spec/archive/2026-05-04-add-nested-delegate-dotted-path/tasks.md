# Tasks: 嵌套 delegate dotted-path 类型访问（D-6）

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04

## 进度概览
- [ ] 阶段 1: AST + Parser
- [ ] 阶段 2: SymbolTable.ResolveType MemberType 分支
- [ ] 阶段 3: 测试 + 文档

## 阶段 1: AST + Parser
- [ ] 1.1 `src/compiler/z42.Syntax/Parser/Ast.cs` 加 `sealed record MemberType(TypeExpr Left, string Right, Span Span) : TypeExpr`
- [ ] 1.2 `src/compiler/z42.Syntax/Parser/TypeParser.cs` 在 NamedType / GenericType parse 后 lookahead `.` + Ident 循环 wrap MemberType（左结合）
- [ ] 1.3 处理 `Outer.Inner<T>` —— GenericType 包装在 MemberType 之外
- [ ] 1.4 单元测试 `TypeParserTests.cs` 加 dotted-path / generic-after-dotted 用例

## 阶段 2: SymbolTable.ResolveType MemberType 分支
- [ ] 2.1 验证 `Z42ClassType` 是否暴露 NestedDelegates；若未暴露，从 SymbolCollector 把现有 qualified-key map 引用上来
- [ ] 2.2 `SymbolTable.cs` 加 `ResolveMemberType(MemberType mt)`：resolve Left → 若是 ClassType → lookup Right in NestedDelegates → 返回 signature；不存在报 E0401；左非 class 报 TypeMismatch
- [ ] 2.3 集成入 `ResolveType` switch（与 NamedType / GenericType 并列）

## 阶段 3: 测试 + 文档
- [ ] 3.1 NEW `src/compiler/z42.Tests/Semantics/NestedDelegateAccessTests.cs`：外部字段 ✅、参数 ✅、返回类型 ✅、不存在 nested 名 ❌ E0401、左非 class ❌、`Outer.Inner<T>` 报清晰错误（暂不支持）
- [ ] 3.2 NEW `src/runtime/tests/golden/run/nested_delegate_dotted/source.z42` + `expected_output.txt`：定义 nested delegate + 外部 dotted 引用 + 调用一次输出
- [ ] 3.3 `docs/design/delegates-events.md` 嵌套 delegate 章节加 dotted-path 落地说明（Open Question 1 已答）
- [ ] 3.4 `docs/design/language-overview.md` 类型表达式语法段加 MemberType 说明
- [ ] 3.5 `docs/deferred.md` 移除 D-6 条目

## 阶段 4: 验证
- [ ] 4.1 `dotnet build src/compiler/z42.slnx && cargo build --manifest-path src/runtime/Cargo.toml` —— 无编译错误
- [ ] 4.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 全绿（含 NestedDelegateAccessTests + TypeParserTests 增量）
- [ ] 4.3 `./scripts/test-vm.sh` —— 全绿（含新 golden）
- [ ] 4.4 spec scenarios 5 个场景逐条对应 ✅
- [ ] 4.5 文档同步：`docs/design/delegates-events.md`、`docs/design/language-overview.md`、`docs/deferred.md`

## 备注
- MemberType 是新 AST 节点，不复用 NamedType（Decision 1）
- 左结合解析 A.B.C → MemberType(MemberType(A, B), C)（Decision 2）
- 1 层嵌套支持，深嵌套报清晰错误（Decision 3）
- Generic nested delegate parser 接受但 SymbolCollector 不注册，给清晰错误（Decision 4）
