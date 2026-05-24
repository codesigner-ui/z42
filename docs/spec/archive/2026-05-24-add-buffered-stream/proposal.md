# Proposal: add Std.IO.BufferedStream — small-IO batching wrapper

## Why

`Std.IO.Stream` callers can hit two performance shapes today:

1. **Small writes to a slow Stream**: writing 1 byte at a time to a
   FileStream means one syscall per byte. Pathological for log emitters
   that build content char-by-char.
2. **Small reads from a slow Stream**: similar — 1-byte reads from a
   FileStream / NetworkStream cause one syscall per read.

The .NET / Java / Rust playbook is `BufferedStream(inner, bufferSize)`:
a transparent Stream wrapper that aggregates small Reads / Writes into
larger underlying calls.

This spec adds it.

## What Changes

`Std.IO.BufferedStream` — Stream subclass wrapping another Stream:

```z42
public class BufferedStream : Stream {
    public BufferedStream(Stream inner);                  // default 4 KB buffer
    public BufferedStream(Stream inner, int bufferSize);

    public override bool CanRead();   // inner.CanRead()
    public override bool CanWrite();  // inner.CanWrite()
    public override bool CanSeek();   // inner.CanSeek()

    public override int  Read(byte[] buf, int off, int n);
    public override void Write(byte[] buf, int off, int n);
    public override void Flush();    // drains write-side buffer
    public override void Close();    // Flush then drop reference (does NOT close inner)

    // Position / Length / Seek delegate to inner after flushing buffers
}
```

**Implementation strategy** — single buffer that serves either reading
OR writing at any moment (not both simultaneously). State machine:
`Idle` / `Reading` (buffer holds bytes from inner) / `Writing`
(buffer holds bytes pending push to inner). Switching directions
flushes / discards the buffer:

- `Write` while in `Reading` → discard read buffer (advance inner if
  caller mis-positions) → enter `Writing`
- `Read` while in `Writing` → Flush write buffer → enter `Reading`

This is what .NET's `BufferedStream` does internally; it's simpler than
"separate read and write buffers" and matches the common case (open
file for read, or open file for write, not both at once).

Lifecycle: matches `CompressionEncoderStream` / `StreamWriter` —
`Close()` calls `Flush()` then drops the inner reference but **does
NOT** call `inner.Close()`. Caller owns inner's lifecycle.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.io/src/BufferedStream.z42` | NEW | the class |
| `src/libraries/z42.io/tests/buffered_stream.z42` | NEW | tests (read / write / direction-switch / capability / passthrough seek) |
| `src/libraries/z42.io/README.md` | MODIFY | list new file |
| `docs/design/stdlib/io-stream.md` | MODIFY | flip `io-stream-future-bufferedstream` Deferred → ✅ landed |
| `docs/design/stdlib/roadmap.md` | MODIFY | Stream 索引行 |
| `docs/spec/changes/add-buffered-stream/proposal.md` | NEW | this file |
| `docs/spec/changes/add-buffered-stream/tasks.md` | NEW | task list |

**只读引用**：

- `src/libraries/z42.io/src/Stream.z42` — base class contract
- `src/libraries/z42.io/src/MemoryStream.z42` / `FileStream.z42` —
  test fixtures

## Out of Scope

- **Async** — gated on L3
- **Separate read/write buffers** — single-buffer suffices; can be
  extended later under same API
- **Auto-flush on write** — explicit Flush only (matches .NET default)

## Open Questions

无 — straightforward Stream wrapper, no new abstractions.
