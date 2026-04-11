# Tasks: refactor-compiler-modules
**变更说明：** 将 z42.Compiler 拆分为 z42.Core / z42.Syntax / z42.Semantics，新增 z42.Pipeline
**原因：** 随实现复杂化需清晰分层：基础设施、语法、语义各自独立；编译管线解耦
**文档影响：** 更新 src/compiler/README.md；新增各项目 README.md；无行为变更

## 目录结构（目标）
```
z42.Core/      → Diagnostics (Diagnostic, DiagnosticBag, DiagnosticCatalog)
                 Text (Span)
                 Features (LanguageFeatures)
z42.Syntax/    → Lexer (Token, TokenKind, Lexer, LexCombinators, LexRules, TokenDefs)
                 Parser (Ast, Parser, StmtParser, ExprParser, TopLevelParser, TypeParser)
                 Parser/Core (Combinators, ParseResult, TokenCursor)
z42.Semantics/ → TypeCheck (TypeChecker, TypeEnv, Z42Type, BinaryTypeTable)
                 Codegen (IrGen, IrGenStmts, IrGenExprs)
z42.Pipeline/  → SingleFileCompiler (from Driver/Program.cs: SingleFileDriver)
                 PackageCompiler (from Driver/BuildCommand.cs: compilation logic)
z42.Driver/    → 仅 CLI 路由 (Program.cs + BuildCommand factories)
```

## 命名空间映射
- Z42.Compiler.Diagnostics → Z42.Core.Diagnostics
- Z42.Compiler.Features    → Z42.Core.Features
- Z42.Compiler.Lexer       → Z42.Syntax.Lexer
- Z42.Compiler.Parser      → Z42.Syntax.Parser
- Z42.Compiler.Parser.Core → Z42.Syntax.Parser.Core
- Z42.Compiler.TypeCheck   → Z42.Semantics.TypeCheck
- Z42.Compiler.Codegen     → Z42.Semantics.Codegen
- Span struct (was Z42.Compiler.Lexer) → Z42.Core.Text

## Tasks

- [x] 0.1 创建 openspec tasks.md
- [x] 1.1 创建 z42.Core.csproj + Text/Span.cs + Diagnostics/* + Features/*
- [x] 1.2 创建 z42.Syntax.csproj + Lexer/* + Parser/*
- [x] 1.3 创建 z42.Semantics.csproj + TypeCheck/* + Codegen/*
- [x] 1.4 创建 z42.Pipeline.csproj + SingleFileCompiler.cs + PackageCompiler.cs
- [x] 2.1 更新 z42.Driver: 精简 Program.cs + BuildCommand.cs，引用 Pipeline
- [x] 2.2 更新 z42.Tests.csproj + 所有测试文件的 using
- [x] 2.3 更新 z42.slnx（添加新项目），删除 z42.Compiler/
- [x] 2.4 更新 src/compiler/README.md + 各新项目 README.md
- [x] 3.1 dotnet build —— 无编译错误
- [x] 3.2 dotnet test —— 全绿（381/381）
