# Tasks: fix-z42c-static-call-cross-ns

> 状态：🟢 已完成 | 完成：2026-06-22

**变更说明：** z42c `ExprEmitter` 发静态调用 `Class.m(args)` 时，OwnerClass 限定用
`_ctx.Qualify(OwnerClass)`（按**当前** ns 前缀）而非 `_ctx.QualifyClass(OwnerClass)`
（imported/同包跨-ns aware，查 ImportedClassNs）。→ 同包跨-namespace 静态调用误限定
当前 ns。`Std.Archive.Zip.Write` 调 `Deflate.Compress`（Deflate 在 `Std.Compression`）
→ z42c 发 `Std.Archive.Deflate.Compress$1` → 运行时 undefined。
**原因：** dogfood S3——z42c-built z42.compression `ZipWriteTests` 4 test
`undefined function Std.Archive.Deflate.Compress$1`。C#-built 用 QualifyClassName（imported-aware）正常。
**文档影响：** 无（行为对齐 C#）。

- [x] 1.1 `ExprEmitter.z42` static 分支：`Qualify` → `QualifyClass`（free-function 分支不变；DepIndex 跨包路径在前不受影响）
- [x] 1.2 验证：z42c-built z42.compression Zip deflate write round-trip OK（之前 undefined Std.Archive.Deflate.Compress$1）；compiler-z42 byte-identical 7/7 + 17 units 不回归
- [x] 1.3 归档

## 备注
- QualifyClass 未命中 ImportedClassNs → 回落 Qualify（当前 ns）→ 同-ns 静态调用不变（superset-safe）。
- S3 codegen bug #1/3；#2 blake3 多块 codegen、#3 diagnostics 待续。
