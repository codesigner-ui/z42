# Tasks: `pinned` block syntax (C5)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 进度概览

- [x] 阶段 1: Lexer (`pinned` keyword + TokenKind)
- [x] 阶段 2: AST (PinnedStmt record)
- [x] 阶段 3: Parser (s_table + ParsePinned + SkipToNextStmt)
- [x] 阶段 4: TypeChecker (source type / scope / control flow / mutation / PinnedView fields)
- [x] 阶段 5: IR Codegen (PinPtr / body / UnpinPtr)
- [x] 阶段 6: 单元测试 (Lexer / Parser / TypeCheck)
- [x] 阶段 7: golden e2e + example
- [x] 阶段 8: 文档同步
- [x] 阶段 9: 全绿 + 归档

---

## 阶段 1: Lexer

- [x] 1.1 `src/compiler/z42.Syntax/Lexer/TokenKind.cs`：加 `Pinned` enum 项
- [x] 1.2 `src/compiler/z42.Syntax/Lexer/TokenDefs.cs`：`KeywordDefs` 加 `new("pinned", TokenKind.Pinned, LanguagePhase.Phase2)`
- [x] 1.3 验证：单测 lex `"pinned"` → `TokenKind.Pinned`

## 阶段 2: AST

- [x] 2.1 找到 stmt 节点声明文件（grep `record VarDeclStmt` 或同等）
- [x] 2.2 加 `public sealed record PinnedStmt(string Name, Expr Source, BlockStmt Body, Span Span) : Stmt;`

## 阶段 3: Parser

- [x] 3.1 `StmtParser.s_table` 加 `[TokenKind.Pinned] = new(ParsePinned, ...)` （feature gate 视现状决定）
- [x] 3.2 实现 `private static ParseResult<Stmt> ParsePinned(TokenCursor cursor, Token kw, LanguageFeatures feat)`
- [x] 3.3 `SkipToNextStmt` 加 Pinned 入口识别
- [x] 3.4 单测：解析正常 pinned；缺 `}` / `=` 报 ParseException

## 阶段 4: TypeChecker

- [x] 4.1 引入 `PinnedView` 哨兵 type（在 BuiltinTypes 或同位）—— 不需要 stdlib 注册
- [x] 4.2 `StmtCheck` 加 PinnedStmt case：
  - source 类型校验（仅 string）→ Z0908_NotPinnable
  - body scope 引入 `Name: PinnedView`
  - 设置 "in pinned block" 标记，让 ReturnStmt / BreakStmt / ContinueStmt / ThrowStmt 在该状态下报 Z0908_PinnedControlFlow
  - 如 source 是 simple NameExpr：把 source local 加入 "frozen" 集合，AssignExpr 检测后报 Z0908_PinnedSourceMutated
- [x] 4.3 FieldAccess (`p.ptr` / `p.len`) 在 TypeChecker 识别 PinnedView：
  - `ptr` / `len` → long
  - 其他字段 → 普通 field-not-found 错误（非 Z0908）

## 阶段 5: IR Codegen

- [x] 5.1 `StmtCompiler` 加 PinnedStmt case：
  - 编译 source → srcReg
  - 分配 viewReg (IrType.Ref 或新 IrType.Pinned)
  - emit `PinPtrInstr(viewReg, srcReg)`
  - 在 locals scope 注册 `Name → viewReg`
  - 编译 body
  - emit `UnpinPtrInstr(viewReg)`
- [x] 5.2 FieldGet on PinnedView local 走现有 `FieldGetInstr` —— C4 runtime 已支持

## 阶段 6: 单元测试

- [x] 6.1 创建 `src/compiler/z42.Tests/PinnedSyntaxTests.cs`，覆盖：
  - `Lexer_Pinned_Tokenizes`
  - `Parser_Pinned_BasicForm`
  - `Parser_Pinned_MissingBlock_Errors`
  - `TypeCheck_Pinned_NonStringSource_Z0908`
  - `TypeCheck_Pinned_ReturnInBody_Z0908`
  - `TypeCheck_Pinned_BreakInBody_Z0908`
  - `TypeCheck_Pinned_ThrowInBody_Z0908`
  - `TypeCheck_Pinned_ReassignSource_Z0908`
  - `Codegen_Pinned_EmitsPinPtrUnpinPtr`
  - `TypeCheck_Pinned_LenPtrFields_Long`

## 阶段 7: golden e2e + example

- [x] 7.1 创建 `tests/golden/run/pinned_basic/source.z42`：含 pinned 块返回 .len 测试
- [x] 7.2 创建 `tests/golden/run/pinned_basic/expected.txt`：期望输出
- [x] 7.3 创建 `examples/pinned_basic.z42`：用户示例（与 golden 形式相似但加注释）
- [x] 7.4 运行 `./scripts/test-vm.sh` 验证 pinned_basic 通过

## 阶段 8: 文档同步

- [x] 8.1 `docs/design/grammar.peg`：加 pinned-stmt = "pinned" Identifier "=" Expr Block
- [x] 8.2 `docs/design/language-overview.md`：加 pinned 语法描述 + 限制说明
- [x] 8.3 `docs/design/interop.md` §10：C5 行 → ✅
- [x] 8.4 `docs/design/error-codes.md`：Z0908 抛出条件加 TypeChecker 三项
- [x] 8.5 `docs/roadmap.md` Native Interop 表 C5 → ✅

## 阶段 9: 全绿 + 归档

- [x] 9.1 `dotnet build src/compiler/z42.slnx`
- [x] 9.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
- [x] 9.3 `cargo test --workspace --manifest-path src/runtime/Cargo.toml`
- [x] 9.4 `./scripts/test-vm.sh`
- [x] 9.5 输出验证报告
- [x] 9.6 spec scenarios 1:1 对照实现位置
- [x] 9.7 归档 spec/changes/impl-pinned-syntax → spec/archive/2026-04-29-impl-pinned-syntax
- [x] 9.8 commit + push（不含 .claude/settings*.json）

## 备注

- 这是 z42 用户面向的 native interop 第一个 syntax 落地；其他（`[Native(lib=, entry=)]` extended、`extern class T`、`import T from "lib"`、manifest reader）留给后续 spec
- TypeChecker 的 control flow 检查可能需要新增 InPinnedBlock 标志位；如果现有 BreakStmt/ContinueStmt 已有 in-loop 标志位机制，复用 pattern
- pinned 关键字的 grep 全仓确认零冲突在阶段 1 实施前先做
