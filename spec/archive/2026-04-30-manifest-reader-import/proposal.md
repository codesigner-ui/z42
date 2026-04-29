# Proposal: `import T from "lib"` + manifest reader (C11a)

## Why

C2–C10 让 z42 用户能调 native 函数，但仍需**手写**与 native 端对应的声明。这导致：
- 签名漂移不可被 type checker 捕获（C8 之后只在 libffi cif 边界靠运气兜住）
- 大量重复劳动：native 库改一个签名 → 用户代码 N 处同步改
- 不能像 C# 静态语言那样真正做到"native 库是一等公民"

C11a 第一步：**让 z42 编译器能读 `.z42abi` manifest**。后续 C11b 把 manifest 内容合成为脚本可见的 ClassDecl 注入 TypeChecker，让 native 类型成为 z42 用户视角的普通 class。

本 spec 范围：
- `import IDENT from "<lib>";` 顶层语法
- `Z42.Project.NativeManifest` 读取 `.z42abi` JSON
- E0909 ManifestParseError 落地（C1 占位升级）
- AST 携带 import 列表 → 后续 C11b 用

不引入：实际的类合成；与 TypeChecker 的接通；编译器路径自动 emit CallNative —— 全部留给 C11b。

## What Changes

- **Lexer**：新关键字 `Import`（`from` 走 contextual identifier 路径，避免新增第二个关键字污染普通用户标识符）
- **AST**：新增 `NativeTypeImport(string Name, string LibName, Span Span)` 顶层项；`CompilationUnit` 加 `List<NativeTypeImport> NativeImports` 字段
- **Parser**：在 namespace / using 同段识别 `import IDENT from "lib";`
- **`Z42.Project.NativeManifest`** 类：
  - `Read(string path) → ManifestData`
  - 含 abi_version / module / version / library_name / types[] 等字段
  - 解析失败抛 `NativeManifestException(E0909, message, path)`（避免与现有 build-manifest `ManifestException` 命名冲突）
  - schema 只做"必需字段存在 + abi_version == 1"轻量校验；详细 JSON Schema 校验留给 build infra
- **错误码 E0909 启用**：从 C1 占位升级
- **测试**：parser 5 个 + reader 4 个

## Scope

| 文件 | 变更 |
|------|------|
| `src/compiler/z42.Syntax/Lexer/TokenKind.cs` | MODIFY +Import |
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY +"import" Phase1 |
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY +`NativeTypeImport` record；CompilationUnit 加 `NativeImports` 字段 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | MODIFY +Import 检测 + ParseImportStmt |
| `src/compiler/z42.Project/NativeManifest.cs` | NEW 读取器 + ManifestData 模型 |
| `src/compiler/z42.Project/NativeManifestException.cs` | NEW 失败时抛出携带 E0909（避免与已有 ManifestException 命名冲突） |
| `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` | MODIFY +`ManifestParseError = "E0909"` |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY +E0909 catalog |
| `src/compiler/z42.Tests/NativeImportParserTests.cs` | NEW 5 parser 用例 |
| `src/compiler/z42.Tests/NativeManifestReaderTests.cs` | NEW 4 reader 用例 |
| `docs/design/error-codes.md` | MODIFY E0909 启用 |
| `docs/design/interop.md` / `docs/roadmap.md` | MODIFY +C11a 行 |
| `docs/design/grammar.peg` | MODIFY +import-stmt 产生式 |

## Out of Scope (留给 C11b)

- ClassDecl 合成（manifest type → AST）
- TypeChecker 注入合成的类
- IrGen 自动 emit CallNative
- Tier1NativeBinding 自动从 manifest 拼接
- 编译期"用户调用签名 vs manifest 签名"校验
- VM 侧 dlopen 注册时机协议（仍用 test harness 预注册）

## Open Questions

- [ ] **Q1**：manifest 路径解析？
  - 倾向：`Z42_NATIVE_LIBS_PATH` 环境变量 (colon-separated)，缺省 `.`（当前目录）；测试用绝对路径
- [ ] **Q2**：`from` 是关键字还是 contextual identifier？
  - 倾向：**contextual**（parser 识别 text="from"）。理由：避免新关键字污染用户标识符空间；`from` 在 `import` 后位置上下文唯一
