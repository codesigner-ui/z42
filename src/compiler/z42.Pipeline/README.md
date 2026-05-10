# z42.Pipeline

## 职责
编译管线编排：协调 Syntax → Semantics → IR 的完整编译流程。提供单文件和包级别两种编译入口，解耦 CLI 命令路由与编译逻辑。

## 核心文件
| 文件 | 职责 |
|------|------|
| `PipelineCore.cs` | 纯编译核心：无文件 I/O、无控制台输出；提供 `Compile` / `CheckAndGenerate` / `CheckOnly` 三个可单元测试的入口 |
| `SingleFileCompiler.cs` | 单 `.z42` 文件编译驱动：处理 `--dump-tokens/ast/bound/ir`，委托 `PipelineCore` 做 TypeCheck+Codegen |
| `PackageCompiler.cs` | 包级别编译：加载清单 → 多文件编译（via `PipelineCore`）→ 组装 `.zpkg`；含 `CompiledUnit` DTO |
| `AstDumper.cs` | `--dump-ast` 后端：递归打印 `CompilationUnit` 为缩进树（手写 switch；新增 AST 节点漏改触发 default→throw） |
| `BoundDumper.cs` | `--dump-bound` 后端：基于 `BoundExprVisitor`/`BoundStmtVisitor` 打印 typecheck 输出，每行带 `: <Z42Type>` 注解 |
| `CompilerUtils.cs` | 编译管线共享工具方法（如 `Sha256Hex`） |

## 入口点
- `Z42.Pipeline.PipelineCore.Compile(source, fileName, stdlib, features?)` — 纯编译核心（可测试）
- `Z42.Pipeline.PipelineCore.CheckAndGenerate(cu, fileName, stdlib, features?)` — 从已解析 CU 编译
- `Z42.Pipeline.PipelineCore.CheckOnly(cu, depIndex, features?, imported?)` — TypeCheck only（用于 `--dump-bound` 等不需要 IR 的路径）
- `Z42.Pipeline.SingleFileCompiler.Run(...)` — 单文件编译，输出多种格式
- `Z42.Pipeline.PackageCompiler.Run(...)` — 项目构建
- `Z42.Pipeline.PackageCompiler.RunCheck(...)` — 项目类型检查（不输出产物）
- `Z42.Pipeline.PackageCompiler.BuildDepIndex(...)` — 加载依赖索引

## 依赖关系
→ z42.Core（DiagnosticBag）
→ z42.Syntax（Lexer、Parser）
→ z42.Semantics（TypeChecker、IrGen）
→ z42.IR（IrModule、ZbcWriter、ZpkgReader 等）
→ z42.Project（ProjectManifest、ZpkgBuilder）
