# z42.Pipeline

## 职责
编译管线编排：协调 Syntax → Semantics → IR 的完整编译流程。提供单文件和包级别两种编译入口，解耦 CLI 命令路由与编译逻辑。

## 核心文件
| 文件 | 职责 |
|------|------|
| `SingleFileCompiler.cs` | 单 `.z42` 文件完整编译：Lex → Parse → TypeCheck → Codegen → Emit（ir/zbc/zasm）|
| `PackageCompiler.cs` | 包级别编译：加载清单 → 多文件编译 → 组装 `.zpkg`；包含 `CompiledUnit` DTO |

## 入口点
- `Z42.Pipeline.SingleFileCompiler.Run(...)` — 单文件编译，输出多种格式
- `Z42.Pipeline.PackageCompiler.Run(...)` — 项目构建
- `Z42.Pipeline.PackageCompiler.RunCheck(...)` — 项目类型检查（不输出产物）
- `Z42.Pipeline.PackageCompiler.BuildStdlibIndex(...)` — 加载标准库索引

## 依赖关系
→ z42.Core（DiagnosticBag）
→ z42.Syntax（Lexer、Parser）
→ z42.Semantics（TypeChecker、IrGen）
→ z42.IR（IrModule、ZbcWriter、ZpkgReader 等）
→ z42.Project（ProjectManifest、ZpkgBuilder）
