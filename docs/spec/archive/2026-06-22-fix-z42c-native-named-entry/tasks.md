# Tasks: fix-z42c-native-named-entry

> 状态：🟢 已完成 | 完成：2026-06-22

**变更说明：** z42c `IrGen._nativeIntrinsic` 只识别 positional `[Native("__name")]`
（`Args[0] is StringLitExpr`）。对 **named 短形** `[Native(lib = "L", entry = "E")]`
（named args = `AssignExpr`，z42.compression 8 文件全用）返回 ""，导致 IrGen 不发
builtin 桩函数（line 156 `if (nat != "")`）→ extern 方法 `_CompressRaw` 等在 zpkg 中
无函数体 → 运行时 `undefined function`。
**原因：** dogfood S3——z42c-built z42.compression 在 standalone z42vm 全 18 compression
test `undefined function _CompressRaw`（C#-built 同 vm 正常）。镜像 C# `EmitNativeStub`
短形（`TypeName==null` → `BuiltinInstr(entry)`，经 dlopen 的 ext_builtins 解析）。
**文档影响：** docs/design/compiler/self-hosting.md（受限/native 端口记录）。

- [x] 1.1 `IrGen._nativeIntrinsic`：扫 named args（`AssignExpr` Target=IdentExpr Name=="entry" → StringLit）取 entry 值
- [x] 1.2 验证：z42c-built compression brotli round-trip standalone z42vm OK（之前 undefined）；full gate compression 18→8（剩 8 = 3 个**新** z42c codegen bug，非本 fix）；compiler-z42 byte-identical 7/7 + 17 units 不回归
- [x] 1.3 docs 同步 + 归档

## 备注
- 仅短形（lib+entry，无 type=）；全 named 用例皆 z42.compression。full Tier1（type= → CallNativeInstr）stdlib 未用，留 follow-up（若需）。
- 这是 replace-csharp S3 的最后阻塞（前 4 bug 已修：TSIG/generic-ctor/strip/blake3）。
