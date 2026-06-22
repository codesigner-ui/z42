# Tasks: port-z42c-strip-symbols

> 状态：🟢 已完成 | 完成：2026-06-22

**变更说明：** z42c 发行（--release）构建端口 strip-symbols（split-debug，1.5b）：主 zpkg
MODS `dbug_len=0` + 末尾 BLID（BLAKE3-128 build_id）+ 配套 `.zsym` sidecar（META + STRS(symPool)
+ MDBG + BLID）。镜像 C# `ZpkgWriter.WritePacked(stripSymbols=true)`。
**原因：** replace-csharp S3 最后阻塞——z42c-built release stdlib 无 sidecar →
`StdlibSidecarPairingTests` 失败。strip 是 z42c 显式 deferred feature。
**文档影响：** docs/design/compiler/self-hosting.md + docs/design/runtime/zpkg.md（若 sidecar 格式需记）。

## 实现要点
- build_id = BLAKE3-128（`z42.crypto.Blake3.HashLen(bytes16zeroed, 16)`；z42.crypto 已是 z42c.project dep，零 DEPS 漂移）。
- 主 zpkg STRS **不** intern debug 串（file/var 名）；它们入 sidecar 的 symPool（镜像 C#）。
- DBUG 数据在 release 也要 BUILD（从 irm line tables）→ 进 MDBG sidecar，而非主 zpkg。

- [x] 1.1 `ZpkgWriterZ.WritePackedWithSidecar(zpkg, isRelease)`：release → strip 路径返回 (main, sidecar)；非 release → (main, null)
- [x] 1.2 MODS strip：release 时 dbug_len=0（沿用既有 `!isRelease` 路径）
- [x] 1.3 BLID：append 16B 占位 → assemble → `Blake3.HashLen(main,16)` → patch 末尾 16B
- [x] 1.4 sidecar：symPool（namespace + DBUG file/var 串）+ MDBG + BLID；flags = Packed|SymOnly
- [x] 1.5 `_buildSidecar` + 复用 `ZbcWriter.BuildDbug`（per-module，用 symPool）
- [x] 1.6 driver `Main._build`：release 时写 `<dist>/<name>.zsym`
- [x] 2.1 验证：z42c build stdlib（release）产 22 .zpkg + 22 .zsym；StdlibSidecarPairingTests 2/2
- [x] 2.2 compiler-z42 byte-identical 7/7 + 17 units（z42c.driver **FULLY** byte-identical 含 BLID——依赖 fix-blake3-multichunk-root-flag）
- [~] 2.3 S3 full gate：C# tests（含 sidecar+multicast）+ vm goldens + cross-zpkg + 254/272 stdlib 绿；**剩 18 z42.compression** `_CompressRaw` undefined → 独立 follow-up（self-hosting-future-z42c-compression-native-binding），S3 翻转待其解
- [x] 3.1 docs 同步（self-hosting.md strip 段 + Deferred）+ 归档

## 备注
- 隐藏前置：strip build_id 用 BLAKE3-128，暴露 z42.crypto 多块 bug → fix-blake3-multichunk-root-flag（已归档）。
- strip-symbols 本身完成且验证（byte-identical + sidecar pairing）；S3 翻转阻塞在 compression（独立）。

## 备注
- byte-identical：z42c 自身 7 包主 zpkg 比较 ignore-BLID；strip 后主 zpkg MODS 仍 dbug_len=0（与今相同），STRS 不含 debug 串（与今相同）→ 不回归。sidecar 是新增产物（gate 不比较 z42c 包 sidecar）。
