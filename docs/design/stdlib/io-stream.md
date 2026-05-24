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

### `io-stream-future-filestream`

- **来源**：add-z42-io-stream v0 scope cut
- **触发原因**：FileStream needs to wrap the existing `Std.IO.File`
  primitives + track position + handle large-file `long` offsets.
  Independent spec scope.
- **触发条件**：first real use case beyond `File.ReadAllText` /
  `File.WriteAllText` — e.g. processing a > 100 MB log without
  loading it all into memory.

### `io-stream-future-textreader`

- **来源**：add-z42-io-stream v0 scope cut
- **触发原因**：text-line-oriented I/O (`ReadLine()`, write with
  encoding) needs a layer on top of `Stream` that owns line-buffering
  + encoding decode state. Significant scope — separate spec.
- **触发条件**：first real use case for line-oriented streaming
  (parsing large log files / processing stdin line-by-line).

### `io-stream-future-bufferedstream`

- **来源**：v0 scope cut
- **触发原因**：performance-oriented wrapper that batches small Reads
  / Writes against a slow underlying stream (typical for
  `NetworkStream` / unbuffered `FileStream`).
- **触发条件**：benchmark evidence that small-IO patterns are
  bottlenecked.

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

### `refactor-compression-stream-on-iostream`

- **来源**：v0 scope cut by design — `z42.compression` already
  shipped its own `CompressionStream` shape
- **触发原因**：retrofit `Std.Compression.CompressionStream` to
  extend `Std.IO.Stream` + add `Gzip.WrapStream(Stream src) ->
  Stream` for true on-the-fly pipeline composition.
- **触发条件**：when the user signals "pipeline composition
  matters now" — likely tied to `add-z42-net` HTTP client landing
  (response stream → gzip-decode → text-reader pipeline).

### `refactor-binary-reader-stream`

- **来源**：v0 scope cut
- **触发原因**：`Std.IO.Binary.BinaryReader` / `BinaryWriter` today
  consume `byte[]` only — they should also accept a `Stream` source
  so users can read structured data straight from `FileStream` /
  `NetworkStream` without buffering.
- **触发条件**：first caller that needs to read a structured binary
  format from a file / socket bigger than memory.

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
