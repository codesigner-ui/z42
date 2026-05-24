# Tasks: refactor CompressionStream onto Std.IO.Stream

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24
> 类型：refactor（行为变更 — 旧 CompressionStream API 删除替换）
> Spec：[proposal](proposal.md)
>
> **注**：与并行 add-z42-yaml / add-z42-io-stream 同 caveat — z42.core
> 因并行 session `rename-primitives-to-pascal-case` mid-flight 不可
> 编译，端到端 `./scripts/test-stdlib.sh z42.compression` 待并行 session
> 修好后跑。本 refactor 不改 cdylib C ABI（依然是 v0 的 `__compressor_*`
> builtin），所以底层 Rust 端 21/21 unit tests 不受影响、仍绿。

## 阶段 1: dep + scaffolding

- [ ] 1.1 MODIFY `src/libraries/z42.compression/z42.compression.z42.toml`：
  加 `"z42.io" = "0.1.0"` 到 `[dependencies]`

## 阶段 2: encoder Stream subclass

- [ ] 2.1 NEW `src/libraries/z42.compression/src/CompressionEncoderStream.z42`：
  - `extends Stream`
  - 构造：`(Stream dest, int algo, int level)` — 调 `__compressor_begin(algo, level, false)`
  - `override CanWrite() = true`，其余 capability = false
  - `override Write(buf, off, n)`：copy slice → call `__compressor_feed` → got bytes →
    `_dest.Write(got, 0, got.Length)`
  - `override Flush()`：no-op（DEFLATE 不支持 mid-stream flush in v0；记 deferred）
  - `override Close()`：idempotent；call `__compressor_finish` → tail →
    `_dest.Write(tail, 0, tail.Length)`；clear slot；mark closed
  - thread-local `_closed: bool` 防 double-finish

## 阶段 3: decoder Stream subclass

- [ ] 3.1 NEW `src/libraries/z42.compression/src/CompressionDecoderStream.z42`：
  - `extends Stream`
  - 构造：`(Stream src, int algo)` — stash src + algo，**不**立即 begin
  - `override CanRead() = true`
  - lazy decompress on first `Read`：
    - `_src.ReadAllBytes()` 拿全部 compressed bytes
    - 调 `__compressor_begin(algo, 0, true)`
    - `__compressor_feed(slot, compressedAll)`
    - `__compressor_finish(slot)` → all decompressed bytes
    - 存内部 `_decoded: byte[]` + `_decodedPos: int`
  - 后续 `Read(buf, off, n)`：copy from `_decoded[_decodedPos..]`，advance pos
  - EOF → return 0
  - 文档说明：v0 = "全 buffer + bulk decode"（per compression-future-streaming-decode Deferred）

## 阶段 4: facade WrapWrite / WrapRead methods

- [ ] 4.1 MODIFY `src/libraries/z42.compression/src/Gzip.z42`：
  - REMOVE `CompressStream()` / `CompressStream(int)` / `DecompressStream()`
  - ADD `public static Stream WrapWrite(Stream dest)` →
    `new CompressionEncoderStream(dest, AlgoId.Gzip, Compression.Default)`
  - ADD `public static Stream WrapWrite(Stream dest, int level)` →
    `new CompressionEncoderStream(dest, AlgoId.Gzip, level)`
  - ADD `public static Stream WrapRead(Stream src)` →
    `new CompressionDecoderStream(src, AlgoId.Gzip)`
- [ ] 4.2 MODIFY `Zlib.z42`：同 shape，algo = `AlgoId.Zlib`
- [ ] 4.3 MODIFY `Deflate.z42`：同，algo = `AlgoId.DeflateRaw`
- [ ] 4.4 MODIFY `Zstd.z42`：同，level default = `Compression.ZstdDefault`，
  algo = `AlgoId.Zstd`

## 阶段 5: remove old CompressionStream + tests

- [ ] 5.1 DELETE `src/libraries/z42.compression/src/CompressionStream.z42`
- [ ] 5.2 MODIFY (effective REWRITE) `src/libraries/z42.compression/tests/streaming.z42`：
  - 替换 `CompressionStream.Feed/Finish/Dispose` 老 API
  - 改成 `Gzip.WrapWrite(MemoryStream)` 流式压缩 + read-back 验证

## 阶段 6: 新增 pipeline 测试

- [ ] 6.1 NEW `src/libraries/z42.compression/tests/stream_pipeline.z42`：
  - `Gzip.WrapWrite(MemoryStream)` 写入 plaintext → close → dest.ToArray() →
    `Gzip.Decompress` 验证 round-trip
  - `Gzip.WrapRead(MemoryStream(compressedBytes))` → `ReadAllBytes()` 验证
  - Zstd 同 round-trip
  - 多次小 Write 串起来 vs 一次大 Write 结果等价
  - close 后再 Write → throw（subclass behaviour）

## 阶段 7: docs

- [ ] 7.1 MODIFY `docs/design/stdlib/compression.md`：
  - API 段：删 `CompressionStream` 类描述，加 `WrapWrite / WrapRead` 段
  - 新加 "Pipeline composition" 段 with code 示例
  - Deferred 段保留（streaming-decode 上行未变）
- [ ] 7.2 MODIFY `docs/design/stdlib/io-stream.md`：
  `refactor-compression-stream-on-iostream` Deferred item 改成
  "✅ landed 2026-05-24，详 [refactor-compression-stream-on-iostream archive]"
- [ ] 7.3 MODIFY `docs/design/stdlib/roadmap.md`：Stream 延后项索引 表
  `refactor-compression-stream-on-iostream` 行打勾
- [ ] 7.4 MODIFY `docs/roadmap.md` Deferred Backlog Index：refactor 行
  打勾
- [ ] 7.5 MODIFY `src/libraries/z42.compression/README.md`：
  example 用新 `WrapWrite / WrapRead` shape

## 阶段 8: 验证 + 归档

- [ ] 8.1 `./scripts/test-stdlib.sh z42.compression` 全绿（含新 pipeline test）
- [ ] 8.2 `./scripts/test-stdlib.sh z42.io` 不破（Stream/MemoryStream 上游不变）
- [ ] 8.3 mv `docs/spec/changes/refactor-compression-stream-on-iostream/` →
  `docs/spec/archive/YYYY-MM-DD-refactor-compression-stream-on-iostream/`
- [ ] 8.4 commit + push

## 备注

- z42.core 因并行 session `rename-primitives-to-pascal-case` mid-flight
  暂时不可编译；本 refactor 与 z42.yaml / io-stream 同 caveat，端到端
  test 等并行 session 修好后跑
