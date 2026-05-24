# Proposal: add Std.IO.Stream + MemoryStream

## Why

z42 stdlib lacks a unified `Stream` abstraction. Each module that
needs flow-style I/O currently invents its own shape:

| Module | Streaming API | Shape |
|--------|---------------|-------|
| `z42.compression.CompressionStream` | `Feed / Finish / Dispose` | bespoke slot-based |
| `z42.io.binary.BinaryReader/Writer` | over `byte[]` only | bounded array, not streaming |
| `z42.io.File.ReadAllText` | one-shot | no streaming entry |
| (future) `z42.net` socket | TBD | — |
| (future) `z42.io.process` stdin/stdout | per-`byte[]` already | — |

Two consequences:

1. **No composability**: `Gzip(FileStream)` / `Decompress(NetworkStream)`
   pipelines that .NET / Java / Rust / Go all express naturally aren't
   writable in z42.
2. **API drift across stdlibs**: future net / file-streaming / text-IO
   each gets to repeat the design discussion.

A `Std.IO.Stream` base + one concrete `MemoryStream` (over `byte[]`)
unblocks everything. v0 is small (~200 lines z42, no FFI, no VM
changes); follow-up specs incrementally adopt it across stdlibs.

## What Changes

1. **`Std.IO.Stream`** — concrete base class (z42 has no `abstract`
   keyword yet, so concrete-base-with-throw-stubs is the established
   pattern):
   - Capability predicates: `CanRead() / CanWrite() / CanSeek()`
     (default false; subclass overrides)
   - Core IO: `Read(byte[], offset, count) -> int` (0 = EOF) /
     `Write(byte[], offset, count)`
   - Lifecycle: `Flush() / Close()`
   - Optional position-API: `Length() / Position() / Seek(offset,
     origin)` — base throws `NotSupportedException`; seekable
     subclasses override
   - Convenience helpers built on `Read / Write`: `ReadAllBytes() /
     WriteAllBytes(byte[]) / ReadExactly(int)`
2. **`Std.IO.MemoryStream : Stream`** — `byte[]`-backed stream
   supporting read + write + seek. Two constructors:
   - `MemoryStream()` — empty, writable, growable
   - `MemoryStream(byte[] data)` — read-only view over `data`
   - `ToArray() -> byte[]` — snapshot of current bytes
3. **`Std.IO.SeekOrigin`** — small static class with `Begin = 0 /
   Current = 1 / End = 2` constants for `Seek`'s `origin` param
   (mirrors .NET; no real enum since z42's int-constant pattern is
   what we use elsewhere).
4. **Tests + design doc.**
5. **Out of scope** (follow-up specs):
   - `FileStream` — `add-z42-io-filestream`
   - `NetworkStream` — folds into `add-z42-net`
   - Refactor `CompressionStream` to extend `Stream` —
     `refactor-compression-stream-on-iostream`
   - Refactor `BinaryReader/Writer` to accept `Stream` —
     `refactor-binary-reader-stream`
   - `TextReader / TextWriter` (encoded text I/O) —
     `add-z42-io-textreader`
   - `BufferedStream` wrapper — `add-z42-io-bufferedstream`
   - Async variants — gated on L3 `async/await` syntax

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.io/src/Stream.z42` | NEW | concrete base class |
| `src/libraries/z42.io/src/MemoryStream.z42` | NEW | concrete subclass |
| `src/libraries/z42.io/src/SeekOrigin.z42` | NEW | static int constants |
| `src/libraries/z42.io/tests/stream_memory.z42` | NEW | MemoryStream round-trip / seek / capability |
| `src/libraries/z42.io/tests/stream_base.z42` | NEW | base-class throw-stub behaviour |
| `src/libraries/z42.io/tests/stream_convenience.z42` | NEW | ReadAllBytes / WriteAllBytes / ReadExactly |
| `docs/design/stdlib/io-stream.md` | NEW | API + design notes + Deferred |
| `docs/design/stdlib/roadmap.md` | MODIFY | "已落地" + Deferred Backlog Index |
| `docs/design/stdlib/overview.md` | MODIFY | z42.io 增加 Stream 入口 |

**只读引用**：

- `src/libraries/z42.io/src/File.z42` — 看现有 byte[] / string API 形态
- `src/libraries/z42.core/src/Exceptions/NotSupportedException.z42` —
  用于 base class 未支持操作的标准异常
- .NET `System.IO.Stream` — 行为参考（capability 模型、Seek
  semantics）

## Out of Scope

- 任何对现有 `z42.io.binary` / `z42.compression` 的改写 —
  独立 follow-up spec
- `FileStream` —
  独立 spec（需 wrap `z42.io.File` + position 追踪）
- `BufferedStream` —
  独立 spec
- `TextReader / TextWriter` —
  独立 spec（编码 + 行处理）
- async variants — L3 async/await syntax 未就绪
- `Stream.CopyTo(Stream)` —
  v1 加（v0 用户可手写 `while Read; Write` 等价）

## Open Questions

- [ ] **`Position` 类型**：long vs int？.NET 用 long（4 GB+ 文件）。
  z42 也用 long 一致。
- [ ] **EOF 信号**：Read return 0 vs throw `EndOfStreamException`？
  .NET / Java / Go 都用 0 表示 EOF；本 spec 跟。
- [ ] **`Dispose` vs `Close`**：z42 没有 `IDisposable`。用 `Close()`
  + 文档约定（与 `CompressionStream` 现状一致）；将来若引入
  `using` 语法 + IDisposable，迁移路径明确。
- [ ] **写入越界**：`MemoryStream.Write` 越过当前 Length 时自动
  growth（mirror .NET）OR throw？倾向 growth（write semantics
  自然扩张），read 越界 return 0（EOF）。
