# Std.IO.Stream + MemoryStream

Unified flow-style I/O base class. Models the established
.NET `System.IO.Stream` / Java `InputStream`+`OutputStream` /
Rust `Read`+`Write` shape so future stdlibs (`FileStream`,
`NetworkStream`, `BufferedStream`, `TextReader / TextWriter`)
and refactored existing ones (`CompressionStream`, `BinaryReader/Writer`)
all hang off a single contract.

> Spec: [`docs/spec/archive/2026-05-24-add-z42-io-stream/`](../../spec/archive/2026-05-24-add-z42-io-stream/)
> shipped 2026-05-24.

## API

```z42
namespace Std.IO;

public static class SeekOrigin {
    public static int Begin   = 0;
    public static int Current = 1;
    public static int End     = 2;
}

public class Stream {
    // Capability predicates — default false; subclass overrides
    public virtual bool CanRead();
    public virtual bool CanWrite();
    public virtual bool CanSeek();

    // Core I/O — base throws NotSupportedException
    public virtual int  Read(byte[] buffer, int offset, int count);
    public virtual void Write(byte[] buffer, int offset, int count);

    // Lifecycle
    public virtual void Flush();   // base default: no-op
    public virtual void Close();   // base default: no-op

    // Optional position API — base throws NotSupportedException
    public virtual long Length();
    public virtual long Position();
    public virtual long Seek(long offset, int origin);   // returns new absolute pos

    // Convenience (built on Read / Write — subclasses don't override)
    public byte[] ReadAllBytes();
    public void   WriteAllBytes(byte[] data);
    public byte[] ReadExactly(int count);
}

public class MemoryStream : Stream {
    public MemoryStream();                  // empty, writable, growable
    public MemoryStream(byte[] data);       // read-only view (no copy)
    public byte[] ToArray();                // snapshot of current bytes
}
```

## Why `concrete base + throw stubs` instead of `abstract class`

z42 has no `abstract` keyword yet. Choices were:

| Option | Pros | Cons |
|--------|------|------|
| **A. Concrete `Stream` + `throw NotSupportedException` stubs** | Works today, zero language change, matches existing z42 stdlib pattern (e.g. `Object.Equals` default) | A `new Stream()` is constructible but useless — caller error caught at runtime not compile time |
| B. Wait for `add-language-abstract-class` spec | Compile-time safety | Blocks the entire Stream rollout on a language change |
| C. Interface `IStream` (z42 has interfaces) | Compile-time safety on `implement` side | Loses default implementations for `ReadAllBytes` / `WriteAllBytes` / `ReadExactly` — every concrete would re-write them |

Option A wins for v0. When `abstract class` lands, migrating
`Stream` → abstract is a one-line keyword add per method (no API
break for users).

## Why `Read(buffer, offset, count) -> int` instead of `Read() -> byte[]`

Mirrors .NET / Java / Go / Rust: the **caller** owns the buffer, the
stream fills it. Trade-off:

| Return-`byte[]` shape | Buffer-fill shape (chosen) |
|----------------------|----------------------------|
| Ergonomic for one-shot reads | Allocation-free in hot loops (NetworkStream / FileStream reading MB at a time) |
| Wasteful: one heap allocation per chunk | Caller can size buffer per workload |
| Hard to compose with `ReadExactly(n)` accumulator | Natural fit |

The "ergonomic one-shot" case is served by the `ReadAllBytes()` /
`ReadExactly(n)` convenience helpers which **do** return a `byte[]`.

## Why `int Read` returning 0 = EOF instead of `bool TryRead`

.NET / Java / Go all use the "0 = EOF" convention. Familiar for
~every developer; doesn't require an explicit out param.

## Why `Close()` not `Dispose()`

z42 has no `IDisposable` trait yet (and no `using` syntactic sugar).
`Close()` matches the existing `Std.Compression.CompressionStream.Dispose`
shape closely; future migration to a proper `IDisposable` / `using`
story can rename or alias without breaking call sites.

## Composability examples (preview of follow-up specs)

Once future specs land, these all compose naturally:

```z42
// Decompress an in-memory zip entry
byte[] compressed = ...;
MemoryStream src = new MemoryStream(compressed);
Stream gzipped = Gzip.WrapStream(src);          // refactor-compression-stream
byte[] decompressed = gzipped.ReadAllBytes();

// Read first 4-byte little-endian int from a file
FileStream f = new FileStream("config.bin");     // add-z42-io-filestream
BinaryReader r = new BinaryReader(f);            // refactor-binary-reader-stream
int magic = r.ReadInt32();

// Pipe socket bytes through gzip on the fly
NetworkStream sock = ...;                         // add-z42-net-network-stream
Stream gzipped2 = Gzip.WrapStream(sock);
TextReader text = new TextReader(gzipped2, Utf8); // add-z42-io-textreader
string line = text.ReadLine();
```

None of those compositions is writable in z42 today; v0 of this
spec is the foundation that makes them possible.

## Deferred / Future Work

### `io-stream-future-filestream` — ✅ landed 2026-05-24

Landed in spec `add-z42-io-filestream`: `Std.IO.FileStream : Stream`
plus `Std.IO.FileMode` (`Read` / `Write` / `Append`). Backed by 8 new
corelib builtins (`__file_open` / `__file_read` / `__file_write` /
`__file_seek` / `__file_length` / `__file_position` / `__file_flush`
/ `__file_close`) operating on a `VmCore.file_handles` slot table
keyed by monotonic `u64` handle id — same pattern as processes /
mutexes / channels / compressors. Capability matrix per mode: Read
→ `CanRead + CanSeek`; Write → `CanWrite + CanSeek`; Append →
`CanWrite` only (POSIX `O_APPEND` forces writes to EOF so seek would
mislead callers).

### `io-stream-future-textreader`

- **来源**：add-z42-io-stream v0 scope cut
- **触发原因**：text-line-oriented I/O (`ReadLine()`, write with
  encoding) needs a layer on top of `Stream` that owns line-buffering
  + encoding decode state. Significant scope — separate spec.
- **触发条件**：first real use case for line-oriented streaming
  (parsing large log files / processing stdin line-by-line).

### ~~`io-stream-future-bufferedstream`~~ — **✅ landed 2026-05-24**

Shipped via `add-buffered-stream`: `Std.IO.BufferedStream` — Stream
subclass with a single shared buffer that serves either reads OR
writes at any moment (mirrors .NET single-buffer pattern; switching
directions flushes / discards). Default 4 KB buffer. Large
reads/writes (>= bufferSize) bypass the buffer to avoid double-copy.
`Close()` flushes pending writes; does NOT close inner. See
[`docs/spec/archive/2026-05-24-add-buffered-stream/`](../../spec/archive/2026-05-24-add-buffered-stream/).

### `io-stream-future-async`

- **来源**：v0 scope cut
- **触发原因**：`async ReadAsync` / `WriteAsync` requires z42 L3
  `async`/`await` syntax which isn't implemented yet.
- **触发条件**：L3 async lands.

### `io-stream-future-copy-to`

- **来源**：v0 scope cut — a minor convenience helper
- **触发原因**：`Stream.CopyTo(Stream dest)` is a `while Read; Write`
  one-liner; v0 callers write it inline. Adds zero capability, only
  ergonomic gain.
- **触发条件**：repetitive caller patterns in real apps prove its
  worth.

### ~~`refactor-compression-stream-on-iostream`~~ — **✅ landed 2026-05-24**

Shipped: bespoke `Std.Compression.CompressionStream` removed;
replaced by generic `_CompressionEncoderStream` / `_CompressionDecoderStream`
extending `Std.IO.Stream`. Each facade now exposes
`WrapWrite(Stream dest) / WrapWrite(Stream dest, int level) /
WrapRead(Stream src)`. See
[`docs/spec/archive/2026-05-24-refactor-compression-stream-on-iostream/`](../../spec/archive/2026-05-24-refactor-compression-stream-on-iostream/)
+ [compression.md "Pipeline composition"](../stdlib/compression.md#pipeline-composition-wrapwrite--wrapread)
for the post-refactor API.

### ~~`process-stream-stdio`~~ — **✅ landed 2026-05-24**

Shipped: `Std.IO.ProcessStdinStream` (write-only Stream over child stdin)
+ `Std.IO.ProcessOutputStream` (read-only Stream, parameterised by
`_fd` to serve both stdout and stderr — same single-class-multi-mode
pattern as `FileStream`). Backed by 2 new corelib builtins
(`__process_handle_read_stdout` / `_read_stderr`) with the same
buffer-fill shape as `__file_read` (slot, buf, off, count → int;
0 = EOF). `ProcessHandle` gains cached `GetStdinStream()` /
`GetStdoutStream()` / `GetStderrStream()` accessors. After draining
via streaming reads, `Wait()`'s `ProcessResult.StdoutBytes` /
`StderrBytes` reflect only whatever was left in the pipe (typically
empty). See
[`docs/spec/archive/2026-05-24-add-process-stream-stdio/`](../../spec/archive/2026-05-24-add-process-stream-stdio/).

### ~~`refactor-binary-reader-stream`~~ — **✅ landed 2026-05-24**

Shipped: `BinaryReader / BinaryWriter` now back onto `Std.IO.Stream`
internally. Existing `BinaryReader(byte[])` / `BinaryWriter()` /
`BinaryWriter(int initialCapacity)` constructors preserved as
convenience sugar (delegate to `new MemoryStream(...)` internally);
new `BinaryReader(Stream src)` / `BinaryWriter(Stream dest)`
constructors let users pipe through any Stream. `ToArray()` /
`Clear()` on BinaryWriter gated to `_ownsStream` for the
caller-supplied dest path. See
[`docs/spec/archive/2026-05-24-refactor-binary-reader-stream/`](../../spec/archive/2026-05-24-refactor-binary-reader-stream/)
+ [io-binary.md](io-binary.md) for the post-refactor surface.

### ~~`add-z42-io-string-reader-writer`~~ — **✅ landed 2026-05-24**

Shipped: char-oriented `Std.IO.StringReader` (`Peek` / `Read` /
`ReadLine` / `ReadToEnd`) + `Std.IO.StringWriter` (`Write` /
`WriteLine` / `ToString` / `Clear`). Pure-script, no VM changes;
StringWriter backed by `Std.Text.StringBuilder`. Mirrors .NET
`System.IO.StringReader/Writer` and Python `io.StringIO`. Does NOT
extend `Std.IO.Stream` — these are char-oriented; the byte ↔ char
bridge is the future `StreamReader/Writer(Stream, Encoding)`
(deferred — needs an `Encoding` type). See
[`docs/spec/archive/2026-05-24-add-z42-io-string-reader-writer/`](../../spec/archive/2026-05-24-add-z42-io-string-reader-writer/).

### ~~`io-stream-future-streamreader-writer`~~ — **✅ landed 2026-05-24**

Shipped via `add-encoding-and-stream-text`: new `Std.Encoding.Encoding`
concrete class wrapping `Utf8` statics (forward-compatible for future
Latin-1 / UTF-16); `Std.IO.StreamReader(Stream)` / `(Stream, Encoding)`
and `Std.IO.StreamWriter(Stream)` / `(Stream, Encoding)`. v0
StreamReader uses drain-and-decode (slurp all bytes → decode → wrap
in internal StringReader) for correct UTF-8 boundary handling without
a stateful decoder. StreamWriter encodes immediately per Write
(byte-side buffering left to the dest Stream). Both leave the
underlying Stream open on `Close()` (caller owns lifecycle). See
[`docs/spec/archive/2026-05-24-add-encoding-and-stream-text/`](../../spec/archive/2026-05-24-add-encoding-and-stream-text/).

### `io-stream-future-streamreader-chunked`

- **来源**：add-encoding-and-stream-text v0 scope cut
- **触发原因**：v0 `StreamReader` drains the entire source Stream
  before the first Read returns — fine for files / typical config
  payloads, fails for 10 GB log tailing or unbounded streams (sockets
  that never EOF). True chunked decoding needs a stateful
  `Encoding.GetDecoder() -> Decoder` API with
  `Convert(byte[] in, int inOff, int inN, char[] out, int outOff,
  int outN, bool flush) -> (int bytesUsed, int charsProduced,
  bool complete)` shape (mirrors .NET `System.Text.Decoder`) to carry
  partial UTF-8 sequences across chunk boundaries.
- **触发条件**：first real use case for streaming-decode (live log
  tailing / unbounded socket text protocol).

### `io-stream-future-objectdisposed`

- **来源**：v0 scope cut
- **触发原因**：`Std.ObjectDisposedException` doesn't exist; v0
  `Close()` doesn't track a `_closed` flag (Reads after Close are
  undefined-but-typically-throw via null-deref). Cleaner story
  needs a proper `IDisposable` / `using` language story.
- **触发条件**：z42 lands `IDisposable` + `using` syntax.

### `io-stream-future-end-of-stream-exception`

- **来源**：v0 scope cut
- **触发原因**：`ReadExactly` today throws `InvalidOperationException`
  on premature EOF. Should be a dedicated `Std.EndOfStreamException`
  (mirrors .NET) — needs a small `add-z42-io-exceptions` follow-up.
- **触发条件**：first caller that catches EOF specifically
  (vs other `InvalidOperationException` causes).
