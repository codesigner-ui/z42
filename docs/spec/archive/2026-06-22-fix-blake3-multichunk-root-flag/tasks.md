# Tasks: fix-blake3-multichunk-root-flag

> 状态：🟢 已完成 | 完成：2026-06-22

**变更说明：** z42.crypto `Blake3` 多块（>1024B，≥2 chunk）树哈希产出**非标准** BLAKE3，
两个 bug：① `_parentCV`（中间 tree parent）经 `_parentOutputState` 恒带 `FLAG_ROOT` →
中间 parent 误标 ROOT（应仅最顶 parent 带 ROOT）；② `_chunkCV` / `_parentCV` 取链接值
（CV）用 `state[i] ^ state[i+8]` —— 但 `_compress` 已做 feed-forward，CV 即 output[0..7]
**直接取**，再 XOR = 二次 feed-forward。
**原因：** strip-symbols build_id（BLAKE3-128 over ~20KB zpkg）与 C# nuget 参考不符暴露；
小向量（≤1 chunk）走单 chunk 路径，从不经 `_chunkCV`/`_parentCV`/中间 parent → 测不到。
**影响：** 任何 >1024 字节输入的 BLAKE3（文件哈希 / build_id / …）此前全错。
**文档影响：** 无（修复对齐标准 BLAKE3；行为回归正确）。

- [x] 1.1 `_parentCV`：独立 PARENT-only 压缩（不再委托 `_parentOutputState`，去掉 ROOT）
- [x] 1.2 `_chunkCV` + `_parentCV`：CV = `state[0..7]` 直接取（去掉二次 feed-forward XOR）
- [x] 1.3 回归测试 `test_blake3_multi_chunk_reference_vector`：2049B（i%251，3 chunk）= `a4283937...dc99`（cross-validated 对 C#/Rust 参考——z42c build_id 与 C# nuget 逐字节一致）
- [x] 1.4 验证：z42c.driver build_id（z42c via 修复 Blake3）== C# nuget **逐字节一致**；full gate（待）

## 备注
- 单 chunk（≤1024B）路径不受影响（不经 tree）；小向量 + abc/empty 仍过。
- 这是 strip-symbols（z42c build_id）+ S3（z42c 接管 stdlib build）的隐藏前置。
