# Tasks: Native class synthesis (C11b — Path B1)

> 状态：🟢 已完成 | 创建：2026-04-30 | 完成：2026-04-30

## 进度概览
- [x] 阶段 1: 错误码 — E0916 占位 + Catalog
- [x] 阶段 2: 签名解析器 — `ManifestSignatureParser`
- [x] 阶段 3: Locator — `INativeManifestLocator` + `DefaultNativeManifestLocator` + `InMemoryManifestLocator`
- [x] 阶段 4: 合成器 — `NativeImportSynthesizer.Run(cu, locator, sourceDir)`
- [x] 阶段 5: Pipeline 接入 — `PackageCompiler.BuildTarget` source-loop
- [x] 阶段 6: 测试 — sig parser 17（含 InlineData 展开）+ synthesizer 11
- [x] 阶段 7: 文档同步 — error-codes / interop §11.5 + Roadmap / roadmap.md
- [x] 阶段 8: GREEN 验证 + 归档 + commit

---

## 阶段 1: 错误码
- [x] 1.1 `Diagnostic.cs` +`NativeImportSynthesisFailure = "E0916"`
- [x] 1.2 `DiagnosticCatalog.cs` E0916 entry（含 a–d 触发清单）

## 阶段 2: 签名解析器
- [x] 2.1 `src/compiler/z42.Semantics/Synthesis/ManifestSignatureParser.cs`
- [x] 2.2 API：`ParseReturn(sig, selfTypeName, span) → TypeExpr` + `ParseParam(sig, selfTypeName, firstParam, span) → (bool isReceiver, TypeExpr? type)`
- [x] 2.3 白名单：`void` / `i8/i16/i32/i64` / `u8/u16/u32/u64` / `f32/f64` / `bool` / `Self` / `*mut Self` / `*const Self`
- [x] 2.4 不在白名单 → `NativeImportException(E0916)`

## 阶段 3: Locator
- [x] 3.1 `INativeManifestLocator.cs` 接口（含 importSpan，错误归因清晰）
- [x] 3.2 `DefaultNativeManifestLocator`（搜索 sourceDir → `Z42_NATIVE_LIBS_PATH`）
- [x] 3.3 `InMemoryManifestLocator`（test-only，写临时文件复用 `NativeManifest.Read` 的真实 I/O 路径）
- [x] 3.4 `NativeImportException.cs`（Code / Span / Message）

## 阶段 4: 合成器
- [x] 4.1 `src/compiler/z42.Semantics/Synthesis/NativeImportSynthesizer.cs`
- [x] 4.2 主流程：早退 / 冲突预扫描 / manifest 缓存 / 合成 + append
- [x] 4.3 `SynthesizeClass(typeEntry, manifest, span)` → ClassDecl with ClassNativeDefaults
- [x] 4.4 `SynthesizeMethod(methodEntry, selfTypeName, span)` → FunctionDecl，按 kind 分支
  - ctor → name = selfTypeName, ReturnType = VoidType
  - method → 强制第一参数为 `*mut/const Self`，否则 E0916
  - static → Modifiers |= Static
  - Tier1Binding.Entry = symbol，其他字段 null（走类级 default）

## 阶段 5: Pipeline 接入
- [x] 5.1 `PackageCompiler.BuildTarget.cs` Phase 0 解析后插 `NativeImportSynthesizer.Run(cu, DefaultLocator, sourceDir=Path.GetDirectoryName(file))`
- [x] 5.2 异常路径：`NativeImportException` / `NativeManifestException` 都转为 `error[Exxxx]: msg` 写到 stderr，`parseErrors++`，与既有 driver 输出格式一致

## 阶段 6: 测试
- [x] 6.1 `ManifestSignatureParserTests.cs` —— 17 个用例（Theory 12 个 primitive + 6 个签名）
  - `ParseReturn_Primitive_Roundtrips` × 12（InlineData）
  - `ParseReturn_Self_BindsToEnclosingType`
  - `ParseReturn_Unsupported_Throws_E0916` × 4
  - `ParseParam_FirstParam_PointerSelf_IsReceiver` × 2
  - `ParseParam_NonFirstParam_PointerSelf_RejectedAsUnsupported`
  - `ParseParam_PrimitiveParam_NotReceiver`
  - `ParseParam_Unsupported_Throws_E0916`
- [x] 6.2 `NativeImportSynthesizerTests.cs` —— 11 个用例
  - happy: SingleImport / Tier1Entry / Ctor / StaticModifier / DropsReceiver / Empty / MultipleOrderPreserved
  - error: TypeNotInManifest / ConflictingDifferentLib / UnsupportedSignature / ManifestNotFound

## 阶段 7: 文档同步
- [x] 7.1 `docs/design/error-codes.md` +E0916（与 E0909 并列）
- [x] 7.2 `docs/design/interop.md` §10 Roadmap +L2.M13f；新增 §11.5 "Native Class Synthesis: Path B1 / B2 / C"
- [x] 7.3 `docs/roadmap.md` Native interop 表 +C11b 行；C11+ 行内列出 C11c/C11d/C11e 后续

## 阶段 8: 验证 + 归档 + commit
- [x] 8.1 `dotnet build src/compiler/z42.slnx` — 0 warning / 0 error
- [x] 8.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无回归
- [x] 8.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 812/812 通过（C11a 779 + C11b 33）
- [x] 8.4 `./scripts/test-vm.sh` — 208/208 通过
- [x] 8.5 spec scenarios 全部覆盖
- [ ] 8.6 移动 `spec/changes/synthesize-native-class/` → `spec/archive/2026-04-30-synthesize-native-class/`
- [ ] 8.7 单 commit：`feat(compiler): synthesize ClassDecl from .z42abi manifest (C11b, B1)`

## 备注
- 合成 ClassDecl 默认 `internal`、`sealed`、无脚本字段（B1 风味）
- 同名 type + 不同 lib 直接 E0916 报错
- 实施中**未**碰到 IrGen receiver / TypeChecker 合成类成员解析的边角问题——既有 C9 stitching 路径完整覆盖
- C11c / C11d / C11e 留作后续独立 spec
