# z42 Compiler — 模块说明

```
z42.Core        基础设施（无任何业务依赖）
z42.Syntax      语法层（Lexer + Parser + AST）
z42.Semantics   语义层（TypeCheck + Codegen）
z42.IR          共享契约层（IR 模型 + 包格式 + 项目类型）
z42.Project     项目清单管理（.z42.toml 解析 + 源文件发现）
z42.Pipeline    编译管线编排（单文件 + 包级别编译）
z42.Driver      CLI 入口（命令路由）
z42.Tests       测试套件
```

---

## 依赖关系图

```
z42.Tests ──────────────────────────────────────────────────────┐
                                                                 ↓
z42.Driver ──→ z42.Pipeline ──→ z42.Semantics ──→ z42.Syntax ──→ z42.Core
                   └──────────→ z42.IR
                   └──────────→ z42.Project ──→ z42.IR
z42.Semantics ────────────────→ z42.IR
```

`z42.Core` 和 `z42.IR` 是唯一的无内部依赖层，所有其他模块均可安全引用它们。

---

## z42.Core — 基础设施

**无任何业务依赖**。被所有其他模块引用。

| 目录 / 文件 | 职责 |
|-------------|------|
| `Text/Span.cs` | 源码位置范围（字节偏移 + 行列号） |
| `Diagnostics/Diagnostic.cs` | 单条诊断（级别 + 错误码 + 消息 + 位置）+ `DiagnosticCodes` |
| `Diagnostics/DiagnosticBag.cs` | 诊断收集器 + `CompilationException` |
| `Diagnostics/DiagnosticCatalog.cs` | 错误码文档（`explain` 命令）|
| `Features/LanguageFeatures.cs` | 语言特性开关（Minimal / Phase1 profiles）|

---

## z42.Syntax — 语法层

**依赖**：z42.Core

| 目录 | 职责 |
|------|------|
| `Lexer/` | `TokenKind`、`Token`、`Lexer`（执行引擎）、`TokenDefs`（声明表）、`LexCombinators`、`LexRules` |
| `Parser/Core/` | `TokenCursor`（不可变游标）、`ParseResult<T>`、`Combinators`（组合子） |
| `Parser/` | `Ast.cs`（节点类型）、`Parser`（公开入口）、`TypeParser`、`ExprParser`、`StmtParser`、`TopLevelParser` |

**处理流程：** `SourceText → Lexer → Token[] → Parser → CompilationUnit (AST)`

---

## z42.Semantics — 语义层

**依赖**：z42.Core + z42.Syntax + z42.IR

| 目录 | 职责 |
|------|------|
| `TypeCheck/` | `TypeChecker`（两遍检查）、`Z42Type`（类型层次）、`TypeEnv`（符号表）、`BinaryTypeTable` |
| `Codegen/` | `IrGen`（HIR → IR Lowering），分部文件：`IrGenStmts`、`IrGenExprs` |

**处理流程：** `CompilationUnit → TypeChecker → IrGen → IrModule`

---

## z42.IR — 共享契约层

**无内部依赖**。被 z42.Semantics、z42.Project、z42.Pipeline、z42.Driver 共同引用。

| 文件 | 职责 |
|------|------|
| `IrModule.cs` | IR 数据结构（SSA 寄存器形式），编译器输出 / VM 输入 |
| `PackageTypes.cs` | `ZbcFile` / `ZpkgFile` / `ZpkgKind` / `ZpkgMode` 数据模型 |
| `ProjectTypes.cs` | `.z42.toml` 反序列化目标类型（`Z42Proj` / `Z42Sln` 等）|
| `DependencyIndex.cs` | 依赖命名空间 → 函数签名索引 |
| `BinaryFormat/` | `Opcodes`、`ZbcWriter`、`ZbcReader`、`ZasmWriter` |

---

## z42.Project — 项目清单管理

**依赖**：z42.IR（使用 `Z42Proj` / `ProjectMeta` 等类型）

| 文件 | 职责 |
|------|------|
| `ProjectManifest.cs` | 发现、加载 `.z42.toml`；解析多目标；glob 展开源文件列表 |
| `PackageTypes.cs` | `ZpkgFile` 序列化模型（JSON 格式用于 Phase 1）|
| `ZpkgBuilder.cs` | 构建 `.zpkg`（packed / indexed 模式）|
| `ZpkgReader.cs` | 读取 `.zpkg` 元数据、命名空间、模块 |
| `ZpkgWriter.cs` | 序列化 `.zpkg` |

---

## z42.Pipeline — 编译管线

**依赖**：z42.Core + z42.Syntax + z42.Semantics + z42.IR + z42.Project

| 文件 | 职责 |
|------|------|
| `SingleFileCompiler.cs` | 单文件完整编译流程（Lex → Parse → TypeCheck → Codegen → Emit）|
| `PackageCompiler.cs` | 包级别编译（多文件 + `.zpkg` 组装）+ `CompiledUnit` DTO |

---

## z42.Driver — CLI 入口

**依赖**：z42.Pipeline + z42.IR + z42.Core

两个文件：

| 文件 | 职责 |
|------|------|
| `Program.cs` | 命令路由（`build` / `check` / `disasm` / `explain` / `errors` / 单文件模式）|
| `BuildCommand.cs` | `Command` 工厂，委托给 `PackageCompiler.Run` / `RunCheck` |

---

## z42.Tests — 测试套件

| 文件 | 覆盖范围 |
|------|---------|
| `LexerTests.cs` | Token 识别、关键字、符号、数字/字符串字面量 |
| `ParserTests.cs` | AST 节点构造、组合子行为、错误恢复 |
| `TypeCheckerTests.cs` | 类型推断、运算符类型表、错误报告 |
| `IrGenTests.cs` | 字节码生成、寄存器分配、调用类型 |
| `GoldenTests.cs` | 端到端 pipeline：源文件 → 参考输出对比 |
| `GrammarSyncTests.cs` | 语法定义一致性校验 |
| `ProjectManifestTests.cs` | TOML 解析、多目标、glob 展开 |
| `ZbcRoundTripTests.cs` | `ZbcWriter` → `ZbcReader` 字节级往返验证 |
| `ZpkgNamespacesTests.cs` | ZpkgFile 命名空间序列化验证 |
