# Proposal: Std.Encoding.Encoding + Std.IO.StreamReader / StreamWriter

## Why

The Stream ecosystem covers byte I/O end-to-end (Stream / MemoryStream /
FileStream / Process*Stream / Compression*Stream / BinaryReader/Writer),
and char I/O over in-memory strings (StringReader / StringWriter). The
missing piece is the **byte ↔ char bridge** — text I/O over a real byte
stream (file, pipe, network, compressed).

Without it, a caller wanting to "read lines from a UTF-8 file" has to:

```z42
FileStream fs = new FileStream("config.txt");
byte[] bytes = fs.ReadAllBytes();           // load entire file
string text = Utf8.GetString(bytes);        // decode all at once
StringReader r = new StringReader(text);    // wrap in string reader
while ((string line = r.ReadLine()) != null) { ... }
fs.Close();
```

— four lines of glue for what every BCL/stdlib exposes as
`new StreamReader(fs).ReadLine()`.

This spec lands the bridge.

## What Changes

### `Std.Encoding.Encoding` (new — z42.encoding)

A concrete class wrapping a codec. v0 ships only UTF-8 but the API
shape is forward-compatible for future Latin-1 / UTF-16 / ASCII:

```z42
public class Encoding {
    public byte[] GetBytes(string s);
    public string GetString(byte[] bytes);
    public string GetString(byte[] bytes, int offset, int count);

    public static Encoding GetUtf8();   // singleton getter
}
```

Why static `GetUtf8()` not `static Encoding UTF8 = ...;` field — z42's
class-init ordering for static fields holding non-primitive instances
is brittle (we hit similar quirks with the parallel `FD_STDOUT` static
in ProcessOutputStream). Method-call form is unambiguous and zero-cost.

### `Std.IO.StreamReader` (new — z42.io)

```z42
public class StreamReader {
    public StreamReader(Stream source);                       // UTF-8 default
    public StreamReader(Stream source, Encoding encoding);

    public int    Peek();
    public int    Read();
    public int    Read(char[] buffer, int offset, int count);
    public string ReadLine();    // null on EOF
    public string ReadToEnd();
    public void   Close();
}
```

**v0 implementation strategy**: drain-and-decode. On first read, read
all bytes from the source Stream → decode via the Encoding →
materialise an internal `StringReader` → delegate subsequent Peek /
Read / ReadLine / ReadToEnd to it. Trade-off: simple + correct (UTF-8
sequence boundaries can't split because we get the whole byte array
before decoding); cost is "load entire stream into memory before
first Read returns".

True chunked streaming with partial-sequence carry-over is **deferred**
(`io-stream-future-streamreader-chunked`): needs an `Encoding` stateful
decoder API (`Encoding.GetDecoder() -> Decoder` with `Convert(byte[] in,
int inOff, int inN, char[] out, int outOff, int outN, bool flush)`)
which is a substantial separate spec.

### `Std.IO.StreamWriter` (new — z42.io)

```z42
public class StreamWriter {
    public StreamWriter(Stream dest);                         // UTF-8 default
    public StreamWriter(Stream dest, Encoding encoding);

    public void Write(string s);
    public void Write(char[] buffer, int offset, int count);
    public void WriteLine();
    public void WriteLine(string s);
    public void Flush();   // forces buffered chars → encoded bytes → dest.Write
    public void Close();   // Flush + (optionally) close dest (see below)
}
```

**v0 implementation strategy**: each `Write` encodes its argument
immediately via the Encoding and pushes the bytes through to the
destination Stream. No internal char buffering. `Flush()` is a no-op
on the encode path (everything is already encoded); it propagates
`Flush()` to the dest Stream so byte-side buffers (e.g.
CompressionEncoderStream's codec window) get a chance to drain.

**Lifecycle**: `Close()` calls `Flush()` then leaves the destination
Stream open (matches `CompressionEncoderStream`'s `leaveOpen = true`
default — safer for pipeline composition where the same dest may
receive multiple text writers back-to-back). Callers wanting to close
the dest should do so explicitly after closing the writer.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.encoding/src/Encoding.z42` | NEW | Encoding class wrapping Utf8 statics; `GetUtf8()` singleton getter |
| `src/libraries/z42.encoding/tests/encoding.z42` | NEW | Encoding class behaviour tests (round-trip / offset+count GetString) |
| `src/libraries/z42.io/src/StreamReader.z42` | NEW | Stream-of-bytes → char reader (drain-and-decode v0) |
| `src/libraries/z42.io/src/StreamWriter.z42` | NEW | Stream-of-bytes ← char writer (encode-on-write) |
| `src/libraries/z42.io/tests/stream_reader.z42` | NEW | StreamReader tests (UTF-8 from MemoryStream / FileStream) |
| `src/libraries/z42.io/tests/stream_writer.z42` | NEW | StreamWriter tests (UTF-8 to MemoryStream / FileStream) |
| `src/libraries/z42.io/tests/stream_text_pipeline.z42` | NEW | end-to-end: write text to FileStream → read back with StreamReader |
| `src/libraries/z42.io/z42.io.z42.toml` | MODIFY | add `z42.encoding` dep |
| `src/libraries/z42.io/README.md` | MODIFY | list new files |
| `docs/design/stdlib/io-stream.md` | MODIFY | flip Deferred `io-stream-future-streamreader-writer` to ✅ landed; add chunked-streaming Deferred entry |
| `docs/design/stdlib/roadmap.md` | MODIFY | Stream 延后项索引行 |
| `docs/design/stdlib/encoding.md` | MODIFY | add Encoding class section |
| `docs/spec/changes/add-encoding-and-stream-text/proposal.md` | NEW | this file |
| `docs/spec/changes/add-encoding-and-stream-text/tasks.md` | NEW | task list |

**只读引用**：

- `src/libraries/z42.encoding/src/Utf8.z42` — underlying codec
- `src/libraries/z42.io/src/Stream.z42` — base + convenience helpers
  (ReadAllBytes, WriteAllBytes)
- `src/libraries/z42.io/src/StringReader.z42` — internal delegate for
  StreamReader's line/char API
- `src/libraries/z42.io/src/MemoryStream.z42` — test fixture
- `src/libraries/z42.io/src/FileStream.z42` — test fixture for
  end-to-end pipeline

## Out of Scope

- **Polymorphic `Encoding` abstract base** — z42 has no `abstract`
  keyword; current concrete-class shape upgrades cleanly when more
  encodings land (rename `Encoding` → `Utf8Encoding`, extract base, add
  `Latin1Encoding` etc.)
- **Chunked streaming decode** — `Encoding.GetDecoder()` stateful API
  needed (deferred — `io-stream-future-streamreader-chunked`)
- **`StreamWriter` AutoFlush** — .NET property; user calls `Flush()`
  explicitly in v0
- **BOM detection** (`new StreamReader(stream, detectEncodingFromBOM: true)`)
  — needs multi-encoding lineup
- **Newline modes** (`StreamWriter.NewLine = "\r\n"`) — `WriteLine` uses
  `"\n"` only; matches `StringWriter` choice
- **Async** — gated on L3
