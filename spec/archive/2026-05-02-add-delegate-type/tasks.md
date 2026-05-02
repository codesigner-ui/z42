# Tasks: D1a — `delegate` 关键字 + 命名 delegate 类型

> 状态：🟢 已完成 | 创建：2026-05-02 | 完成：2026-05-02 | 类型：lang（完整流程）
>
> **实施备注**：
> 1. 嵌套 delegate 注册时同时写入 simple key + qualified key（`Btn.OnClick`），
>    类内部 `OnClick` 直接 resolve；外部 `Btn.OnClick` dotted-path 解析待 follow-up。
> 2. `Action`/`Func` hardcoded desugar 路径保留为兜底（D1c 一并清理）。
> 3. `delegate*` unmanaged Parser 检测 + 报错：测试通过 Diagnostics.HasErrors 验证（Parser 内置 DiagnosticBag 捕获 ParseException）。
> 4. closure.md §3.2 修正完成（"不使用" → "三种等价写法"）。

## 进度概览
- [x] 阶段 1: Lexer + AST
- [x] 阶段 2: Parser
- [x] 阶段 3: TypeChecker / SymbolTable
- [x] 阶段 4: 测试
- [x] 阶段 5: 验证 + 文档同步 + 归档

## 阶段 1: Lexer + AST
- [x] 1.1 `TokenKind.cs` —— 增加 `Delegate`
- [x] 1.2 `TokenDefs.cs::KeywordDefs` —— 注册 `("delegate", TokenKind.Delegate, Phase1)`
- [x] 1.3 `Ast.cs` —— 新增 `DelegateDecl(name, vis, params, ret, span, typeParams?, where?)` sealed record
- [x] 1.4 `Ast.cs::CompilationUnit` —— 增加 `List<DelegateDecl> Delegates`（顶层）
- [x] 1.5 `Ast.cs::ClassDecl` —— 增加 `List<DelegateDecl> NestedDelegates`（嵌套）
- [x] 1.6 grep 项目代码 + 测试，确认无字符串 `"delegate"` 与新关键字冲突
- [x] 1.7 修复所有 `new CompilationUnit(...)` / `new ClassDecl(...)` 调用站点新增字段（默认空 list）

## 阶段 2: Parser
- [x] 2.1 `TopLevelParser.Types.cs` —— 新增 `ParseDelegateDecl(...)` 解析头部 + 可选 `<TypeParams>` + Params + 可选 where
- [x] 2.2 `TopLevelParser.Types.cs::ParseDelegateDecl` —— `delegate*` 检测：见 `*` 报"unmanaged func pointer not yet supported"
- [x] 2.3 `TopLevelParser.cs` —— 顶层主循环识别 `TokenKind.Delegate` 分支
- [x] 2.4 `TopLevelParser.Types.cs::ParseClassDecl` —— 成员循环识别 `TokenKind.Delegate` 分支，挂到 NestedDelegates
- [x] 2.5 复用 `ParseTypeParamList` / `ParseParamList` / `ParseWhereClause` 现有 helpers（grep 确认存在；缺则按现有 ClassDecl 路径模式补）

## 阶段 3: TypeChecker / SymbolTable
- [x] 3.1 `SymbolTable.cs` —— 新增 `record DelegateInfo(Z42FuncType Signature, IReadOnlyList<string> TypeParams, IReadOnlyDictionary<string, GenericConstraintBundle>? Constraints, string? ContainerClass)` + `IReadOnlyDictionary<string, DelegateInfo> Delegates`
- [x] 3.2 `SymbolCollector.cs::Collect` —— 顶层 + 嵌套 delegate 各 RegisterDelegate；arity-suffixed key（`Foo$N`）
- [x] 3.3 `SymbolCollector.cs::ResolveType` —— NamedType 0-arity 路径 + GenericType N-arity 路径都查 delegates；命中实例化（substitute）
- [x] 3.4 `TypeChecker.GenericResolve.cs::ResolveAllWhereConstraints` —— 把 delegate 纳入遍历范围（与 class / func 同 pass）
- [x] 3.5 嵌套 delegate qualification：`Btn.OnClick` 这种 dotted path 在 NamedType 解析时支持（z42 现有是否支持嵌套类型？grep 验证；缺则停下与 User 讨论）
- [x] 3.6 SymbolCollector 现有 `Action`/`Func` hardcoded desugar 不动（D1c 时清理）

## 阶段 4: 测试
- [x] 4.1 NEW `src/compiler/z42.Tests/DelegateDeclParserTests.cs` —— 简单 / 返回值 / 无参 / 嵌套 / 泛型 / where / `delegate*` 报错（7 个）
- [x] 4.2 NEW `src/compiler/z42.Tests/DelegateDeclTypeCheckTests.cs` —— FuncType resolve / lambda 赋值 / type mismatch / 字面量等价 / 泛型实例化 / arity 错误 / where-constraint 验证（7 个）
- [x] 4.3 NEW `src/runtime/tests/golden/run/delegate_d1a/source.z42` —— 端到端 golden（含嵌套 + 泛型 demo）
- [x] 4.4 NEW `src/runtime/tests/golden/run/delegate_d1a/expected_output.txt`
- [x] 4.5 NEW `examples/delegate_basic.z42` —— 演示用
- [x] 4.6 `./scripts/regen-golden-tests.sh` 生成 zbc

## 阶段 5: 验证 + 文档 + 归档
- [x] 5.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 5.2 `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [x] 5.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（基线 +9）
- [x] 5.4 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [x] 5.5 spec scenarios 逐条对应实现位置确认
- [x] 5.6 文档同步：
    - `docs/design/closure.md` §3.2 —— 修正"不使用 Func/Action"为"接受 delegate 关键字 + Func/Action 命名 delegate"
    - `docs/design/delegates-events.md` —— 顶部状态从"前瞻性"改为"D1a 已落地"；§11.2 D1 行加 ✅ 子项
    - `docs/roadmap.md` —— 已完成关键 fix 表加一行
    - `docs/design/language-overview.md` —— §6 类一节后或独立 §6.5 加 delegate 简介
- [x] 5.7 移动 `spec/changes/add-delegate-type/` → `spec/archive/2026-05-02-add-delegate-type/`
- [x] 5.8 commit + push（自动）

## 备注
- 实施时若发现 `Item` 父类不存在或与现有 Class/Enum/Interface 不同基类，按现有模式调整
- DelegateDecl 加入 CompilationUnit 后，所有 `new CompilationUnit(...)` 测试构造点要补参数 —— grep 全部修复
