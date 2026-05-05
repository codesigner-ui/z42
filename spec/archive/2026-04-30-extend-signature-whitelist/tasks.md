# Tasks: Extend manifest signature whitelist (C11e)

> 状态：🟢 已完成 | 创建：2026-04-30 | 实施：2026-05-04 ~ 2026-05-06

## 进度概览
- [x] 阶段 1: API 扩展 — 加 `knownNativeTypes` 参数
- [x] 阶段 2: c_char 分支
- [x] 阶段 3: 指向其他 import 类型的指针分支
- [x] 阶段 4: 错误信息分类（unknown-type / unsupported-shape）
- [x] 阶段 5: Synthesizer 收集 + 下传 knownNativeTypes
- [x] 阶段 6: 测试 — sig parser +5、synthesizer +3
- [x] 阶段 7: 文档同步
- [x] 阶段 8: GREEN + 归档 + commit

---

## 阶段 1: API 扩展
- [x] 1.1 `ManifestSignatureParser.cs`：`ParseReturn` / `ParseParam` 加 `IReadOnlySet<string> knownNativeTypes` 参数（位于 selfTypeName 之后）
- [x] 1.2 既有调用方（`NativeImportSynthesizer.TranslateSignature`）一并更新

## 阶段 2: c_char 分支
- [x] 2.1 `ParseParam` 在 receiver 检查后立即检测 `*const c_char` / `*mut c_char` → 返回 `NamedType("string")`
- [x] 2.2 `ParseReturn` 检测同前缀 → 抛 E0916 with C11f 提示

## 阶段 3: 指向其他 import 类型的指针分支
- [x] 3.1 添加 helper `TryParsePointerToOther(sig, known, selfName)` —— 识别 `*mut <X>` / `*const <X>` 形态，X 既不是 `Self` 也不是 `c_char`
- [x] 3.2 X 在 known → 返回 NamedType(X)；不在 → 抛 E0916 unknown-type
- [x] 3.3 `ParseReturn` 与 `ParseParam` 都接 helper

## 阶段 4: 错误信息分类
- [x] 4.1 `Unknown(typeName, span)` factory：unknown-type 文案
- [x] 4.2 `Unsupported(sig, position, knownTypes, span)` factory：unsupported-shape 文案 + 列已 import 的 type 名
- [x] 4.3 替换原 `Unsupported` 调用点

## 阶段 5: Synthesizer 接入
- [x] 5.1 `NativeImportSynthesizer.Run` 在冲突预扫描后构建 `knownTypes`（含所有 import.Name）
- [x] 5.2 `SynthesizeClass` / `SynthesizeMethod` / `TranslateSignature` 串下 knownTypes
- [x] 5.3 `selfTypeName` 隐式加入 known（imports 集合天然含 selfTypeName，无需额外 seed）

## 阶段 6: 测试
- [x] 6.1 `ManifestSignatureParserTests.cs` 加：
  - `ParseParam_CChar_AsString_Param` × 2 (const + mut) ✓
  - `ParseReturn_CChar_Throws_E0916_WithC11fHint` × 2 ✓
  - `ParseParam_PointerToOtherImported_AsNamedType` × 2 ✓
  - `ParseReturn_PointerToOtherImported_AsNamedType` × 2 ✓
  - `ParseParam_PointerToUnknownType_Throws_UnknownType_E0916` ✓
  - `ParseParam_ConstAndMut_Equivalent_ForOtherType` ✓
  - `ParseReturn_UnsupportedShape_ListsImportedTypes` ✓
  - 既有 `ParseReturn_Unsupported_Throws_E0916` 拆分为 `ParseReturn_UnsupportedShape_Throws_E0916`（去掉 `*const c_char` 重叠）
  - 既有 `ParseParam_Unsupported_Throws_E0916` 改为 `ParseParam_UnsupportedShape_Throws_E0916`（用 `Box<Counter>` 替代 c_char）
- [x] 6.2 `NativeImportSynthesizerTests.cs` 加：
  - `Synth_Method_With_CCharParam_BecomesString` ✓
  - `Synth_Method_Returning_OtherImportedType` ✓
  - `Synth_Method_Param_OtherType_NotImported_E0916` ✓
- [x] 6.3 `dotnet test` 全套 1012/1012 绿

## 阶段 7: 文档同步
- [x] 7.1 `docs/design/error-codes.md` E0916 行更新（C11b → C11b+C11e；加 unknown-type / unsupported-shape 子分类；c_char return 占位 C11f）
- [x] 7.2 `docs/design/interop.md` Roadmap 表 +L2.M13g 行（C11e）；§11.5 加 "Signature whitelist (C11e extension)" 子节 + Out-of-scope C11f 列表
- [x] 7.3 `docs/roadmap.md` Native interop 表 +C11e 行；C11+ 剩余清单更新（C11e 移出，C11f 增补 ownership 协议 + Array/Option/定长数组）

## 阶段 8: GREEN + 归档 + commit
- [x] 8.1 `dotnet build src/compiler/z42.slnx` —— 0 错 0 警
- [x] 8.2 `cargo build --manifest-path src/runtime/Cargo.toml` —— 0 错 0 警
- [x] 8.3 `dotnet test ...` 1054/1054 全绿（C11e 增 +14 测试，加上并行 session 增量）
- [x] 8.4 `./scripts/test-vm.sh` 264/264 全绿（之前 2 个 math fail 是 stale golden zbc，regen-golden 后修复）
- [x] 8.5 spec scenarios 全覆盖（11 个 → 单测 9 个 + e2e 3 个）
- [x] 8.6 `spec/changes/extend-signature-whitelist/` → `spec/archive/2026-04-30-extend-signature-whitelist/`
- [x] 8.7 commit：`feat(compiler): extend native sig whitelist — c_char + cross-imports (C11e)`

## 备注
- c_char return ownership 协议留 C11f
- Array / Option / 定长数组 留 C11f / C11g
