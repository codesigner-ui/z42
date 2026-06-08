# Tasks: compression-decoder-pull-mode

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat (stdlib, 纯 z42)
> 模式：minimal —— 升级一个 Stream 类的内部行为，公开 API 不变，无 lang/ir/vm。
> 子系统锁：`stdlib`（与 add-reflection-mvp 例外共存，仅动 z42.compression，文件零重叠）。

## 背景

cdylib 的流式 decoder 早在 **2026-05-27 (`add-compression-streaming-decode`)** 就
落地了（`flate2::write::*Decoder` / `zstd::stream::write::Decoder`，`compressor_feed`
每 chunk 增量产出）。但 z42 的消费端 **`CompressionDecoderStream` 当时没跟进** ——
仍是 v0 lazy-bulk：首次 `Read` 即 `_src.ReadAllBytes()` → 一次性 `feed` →
`finish`，全量解压结果驻留内存。compression.md 的 2026-05-27 条目曾声称"WrapRead
消费端已是 chunk-by-chunk"，**与实际不符**（消费端仍 materialise 全量）。

本变更补上 z42 消费端，让流式解压**真正端到端**：多-GB / 网络流不再在首个
`Read` 返回前 materialise 整个解压输出。

## 设计（API 不变，per-chunk pull）

`CompressionDecoderStream`：
- 惰性 `_CompressorBegin`（首次 Read），slot 跨多次 Read **持有**
- 每次 Read：先服务内部 `_outBuf[_outPos..]`；耗尽且未 finished → 从 `_src` 读一个
  CHUNK（64 KiB）→ `_CompressorFeed` 拿**增量**输出 → 追加缓冲；`_src` 读到 0 →
  `_CompressorFinish` 拿尾部 → finished。按 `nRead` 切片喂入（Stream.Read 可能 underfill）。
- EOF（finished 且 outBuf 耗尽）→ Read 返回 0
- `Close`：slot 仍持有且未 finish → 调 `_CompressorFinish` 释放（吞输出/错误，
  early-abort 时 incomplete-stream finish 报错属预期）；Read-after-Close 抛
  InvalidOperationException

内存边界：一个压缩 CHUNK + 单 chunk 解压输出，不再 materialise 全量。

## 任务

- [x] 1. 重写 `CompressionDecoderStream.z42`：per-chunk pull；删 stale "v0 lazy bulk" 注释
- [x] 2. 测试 `tests/compression_streaming_decode.z42`（7 个）：gzip/zstd 小 count 增量
      Read · 限流源 `_ThrottledSource`（每 Read ≤N 字节）强制多 feed 轮 · 空输入 EOF ·
      大于剩余的 count · Close-before-EOF 释放 slot + Read-after-Close 抛异常
- [x] 3. 文档：CompressionDecoderStream.z42 注释改述真流式；compression.md 修正
      2026-05-27 条目的"消费端已 chunk-by-chunk"过度声称（标注消费端迁移 2026-06-09）；
      roadmap line 355 标 ✅（cdylib 2026-05-27 + 消费端 2026-06-09）
- [x] 4. GREEN：隔离验证（单包编 z42.compression vs last-good z42.core.zpkg → cp flat
      dist → test lib z42.compression：**11/11**，含我 7 个 + 现有 tar-over-gzip-stream
      集成）。canonical workspace gate 因并发 add-reflection-mvp 的 z42.core 源破损暂不可跑。
- [x] 5. commit + push + 释锁归档

## 备注

cdylib（z42-compression crate）**无需改动**——2026-05-27 已支持流式。本变更纯 z42，
仅让消费端真正按 `_CompressorFeed` 的增量返回值 per-chunk 消费（v0 忽略它、靠 finish
拿全量）。
