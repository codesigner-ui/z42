# Tasks: add Stream.CopyTo(Stream) convenience helper

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：feat (minor — single helper on existing class)

**变更说明**：Add `Stream.CopyTo(Stream dest)` and `CopyTo(Stream dest,
int bufferSize)` to the base Stream class. Subclasses don't need to
override — defaults to a `while Read; Write` loop sized to a 4 KB
buffer.

**原因**：Recurring pattern in tests / examples — drain a FileStream
into a MemoryStream, pipe one Stream into a compression sink, copy a
network response to disk. Currently every caller writes the same 6
lines of `while Read > 0; dest.Write` glue. Mirrors .NET
`Stream.CopyTo(Stream)`.

**文档影响**：io-stream.md flip `io-stream-future-copy-to` Deferred →
✅ landed.

## Tasks

- [x] 1.1 MODIFY `src/libraries/z42.io/src/Stream.z42` — add
      `CopyTo(Stream dest)` (4 KB buffer default) and
      `CopyTo(Stream dest, int bufferSize)` (custom). Body: `while
      Read > 0 → dest.Write`. Pre-checks: `CanRead` on self,
      `CanWrite` on dest, `bufferSize > 0`.
- [x] 2.1 NEW `src/libraries/z42.io/tests/stream_copy_to.z42` —
      tests: MemoryStream → MemoryStream / FileStream → MemoryStream /
      empty source / non-readable source throws / non-writable dest
      throws / custom buffer size.
- [x] 3.1 MODIFY `docs/design/stdlib/io-stream.md` — flip Deferred →
      ✅ landed.
- [x] 3.2 MODIFY `docs/design/stdlib/roadmap.md` — Stream 索引行.
- [x] 4.1 Tests green
- [x] 4.2 Archive + commit + push
