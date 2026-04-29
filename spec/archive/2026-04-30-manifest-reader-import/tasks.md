# Tasks: `import T from "lib"` + manifest reader (C11a)

> 状态：🟢 已完成 | 创建：2026-04-28 | 完成：2026-04-30

## 进度概览
- [x] 阶段 1: Lexer — `import` Phase1 关键字
- [x] 阶段 2: AST — `NativeTypeImport` + `CompilationUnit.NativeImports`
- [x] 阶段 3: Parser — `ParseImport` + 顶层识别
- [x] 阶段 4: Manifest reader — `NativeManifest` / `ManifestData` / `NativeManifestException`
- [x] 阶段 5: 错误码 — E0909 启用
- [x] 阶段 6: 测试 — parser 6 + reader 5 + lexer 1
- [x] 阶段 7: 文档同步 — error-codes / interop / roadmap / grammar
- [x] 阶段 8: GREEN 验证 + 归档 + commit

---

## 阶段 1: Lexer
- [x] 1.1 `src/compiler/z42.Syntax/Lexer/TokenKind.cs` +`Import`
- [x] 1.2 `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` 注册 `"import"` Phase1 keyword

## 阶段 2: AST
- [x] 2.1 `src/compiler/z42.Syntax/Parser/Ast.cs`：新增 `NativeTypeImport(Name, LibName, Span)` record
- [x] 2.2 `CompilationUnit` 末尾追加 `List<NativeTypeImport>? NativeImports = null`

## 阶段 3: Parser
- [x] 3.1 `src/compiler/z42.Syntax/Parser/TopLevelParser.cs`：在 `ParseCompilationUnit` 主循环识别 `TokenKind.Import` 分支
- [x] 3.2 `ParseNativeTypeImport(ref cursor)`：Identifier name → contextual `from` → string-literal lib → `;`
- [x] 3.3 收集到 `CompilationUnit.NativeImports`（空列表时存 null）
- [x] 3.4 `SkipToNextDeclaration` 把 `Import` 加入恢复点

## 阶段 4: Manifest reader
- [x] 4.1 `src/compiler/z42.Project/NativeManifest.cs`：`Read(path)` + `JsonPropertyName` snake_case 映射
- [x] 4.2 `ManifestData` + `TypeEntry` + `FieldEntry` + `MethodEntry` + `ParamEntry` + `TraitImplEntry` + `TraitImplMethod`
- [x] 4.3 `src/compiler/z42.Project/NativeManifestException.cs`：`Code` / `Path` / `Message`（避开既有 `ManifestException`）
- [x] 4.4 校验：`File.Exists` / JSON parse / `abi_version == 1` / 必需字段（`module` / `library_name` / `types`）非空

## 阶段 5: 错误码
- [x] 5.1 `Diagnostic.cs`：`ManifestParseError = "E0909"`
- [x] 5.2 `DiagnosticCatalog.cs`：E0909 entry

## 阶段 6: 测试
- [x] 6.1 `NativeImportParserTests.cs`：6 个用例（lexer 1 + parser 5）
- [x] 6.2 `NativeManifestReaderTests.cs`：5 个用例（valid / missing-file / malformed / abi-mismatch / missing-required）
- 备注：未引入 Fixtures/json 文件 — 测试用 `Path.GetTempPath()` + `Guid.N` 生成隔离临时文件，更hermetic。

## 阶段 7: 文档同步
- [x] 7.1 `docs/design/error-codes.md` E0909 状态从 reserved → enabled（含 a/b/c/d 触发清单）
- [x] 7.2 `docs/design/interop.md` §10 Roadmap 加 L2.M13e（C11a） 行
- [x] 7.3 `docs/roadmap.md` Native interop 表加 C11a 行
- [x] 7.4 `docs/design/grammar.peg` `compilation_unit` 接 `import_decl`，新增 `import_decl` 产生式

## 阶段 8: 验证 + 归档 + commit
- [x] 8.1 `dotnet build src/compiler/z42.slnx` — 0 warning / 0 error
- [x] 8.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无回归
- [x] 8.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 779/779 通过
- [x] 8.4 `./scripts/test-vm.sh` — 208/208 通过（interp + jit 各 104）
- [x] 8.5 spec scenarios 全部覆盖
- [ ] 8.6 移动 `spec/changes/manifest-reader-import/` → `spec/archive/2026-04-30-manifest-reader-import/`
- [ ] 8.7 单 commit：`feat(compiler): import statement + .z42abi manifest reader (C11a)`

## 备注
- C11a 只做"数据通路"，不接通 TypeChecker；CompilationUnit.NativeImports 暂不被任何下游消费，仅由测试断言其内容。
- 实施中发现既有 `Z42.Project.ManifestException`（build-manifest WSxxx 用）与新建类名冲突；按"NativeManifestException"重命名，proposal/design/spec/tasks 同步更新（Scope 文件数不变）。
- C11b 将引入 ClassDecl 合成 + 路径解析 + 编译期签名校验。
