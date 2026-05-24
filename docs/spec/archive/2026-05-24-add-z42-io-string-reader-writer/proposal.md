# Proposal: add Std.IO.StringReader + StringWriter

## Why

The `Std.IO.Stream` ecosystem (Stream / MemoryStream / FileStream /
Process*Stream / Binary{Reader,Writer} / Compression{Encoder,Decoder}Stream)
is now established for **byte-oriented** I/O. But every real text-handling
script (config parsers, log generators, golden test fixtures, REPLs) hits
a gap:

- **`StringBuilder`** owns a growable `string` but only emits text in
  one shot via `ToString()` — no incremental "feed me a line at a time"
  shape, no `ReadLine()` consumer for produced output
- **`MemoryStream(byte[])`** is byte-oriented; converting `byte[]` ↔
  `string` ↔ `string[]`-of-lines pollutes callers with manual UTF-8
  + line-split logic for what is conceptually a one-liner

This spec adds the **textual mirror** of `MemoryStream` — `StringReader`
(read characters/lines from a string) + `StringWriter` (write
characters/lines into a string buffer). These compose naturally with
future `StreamReader(Stream, Encoding)` / `StreamWriter(Stream, Encoding)`
that bridge text and byte streams (deferred — needs an Encoding type).

Out of scope (deferred):

- No `TextReader` / `TextWriter` abstract base class — z42 has no
  `abstract` keyword and the only consumers in v0 are these two
  concrete classes; introducing a base now would be premature
  abstraction. When `StreamReader`/`StreamWriter` land they'll define
  the shared shape; the polymorphism extraction happens then
- No encoding integration (`StringReader.ReadAsBytes(Encoding)` etc.)
  — the value of `StringReader` is to read from a string already in
  memory; if you need bytes, use `Encoding.UTF8.GetBytes(s)` + `MemoryStream`

## What Changes

- New z42 class `Std.IO.StringReader`:
  - `StringReader(string source)` — wraps a string, cursor at 0
  - `Peek() -> int` — next char as int code, -1 = EOF
  - `Read() -> int` — consume next char as int code, -1 = EOF
  - `Read(char[] buf, int offset, int count) -> int` — bulk read,
    returns actually-read count (0 = EOF). Same buffer-fill shape as
    `Stream.Read`
  - `ReadLine() -> string` — read until `\n` / `\r\n` / EOF; returns
    null on EOF, empty string for a blank line. Strips the terminator
  - `ReadToEnd() -> string` — return remaining content as one string
  - `Close()` — release the source ref; subsequent ops throw
  - No `CanRead` etc. — flat API, .NET-style; the `Std.IO.Stream`
    capability protocol is for byte streams

- New z42 class `Std.IO.StringWriter`:
  - `StringWriter()` — empty buffer
  - `StringWriter(int initialCapacity)` — pre-sized buffer
  - `Write(char c)` / `Write(string s)` / `Write(char[] buf, int off, int n)`
  - `WriteLine()` (writes `\n`) / `WriteLine(string s)` / `WriteLine(char c)`
  - `ToString() -> string` — snapshot current buffer
  - `Clear()` — reset buffer to empty
  - `Close()` — no-op (in-memory only); included for API symmetry with
    StringReader
  - Internally backed by `Std.Text.StringBuilder` (chunked-list, already
    proven for the StringBuilder migration)

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.io/src/StringReader.z42` | NEW | StringReader class with Peek / Read / ReadLine / ReadToEnd / Close |
| `src/libraries/z42.io/src/StringWriter.z42` | NEW | StringWriter class with Write* / WriteLine* / ToString / Clear |
| `src/libraries/z42.io/tests/string_reader.z42` | NEW | unit tests (read chars, read lines, EOL variants, EOF, close) |
| `src/libraries/z42.io/tests/string_writer.z42` | NEW | unit tests (write chars / strings / lines, ToString, Clear) |
| `src/libraries/z42.io/tests/string_reader_writer_pipeline.z42` | NEW | composition: StringWriter → ReadAll → StringReader → lines |
| `src/libraries/z42.io/src/Stream.z42` | MODIFY | docstring note: text I/O is StringReader/StringWriter (this spec) + future StreamReader/Writer |
| `src/libraries/z42.io/README.md` | MODIFY | list new files |
| `docs/design/stdlib/io-stream.md` | MODIFY | add ✅ landed section + Deferred entry for StreamReader/Writer |
| `docs/design/stdlib/roadmap.md` | MODIFY | Stream 延后项索引 — add row |
| `docs/spec/changes/add-z42-io-string-reader-writer/proposal.md` | NEW | this file |
| `docs/spec/changes/add-z42-io-string-reader-writer/tasks.md` | NEW | task list |

**只读引用**：

- `src/libraries/z42.text/src/StringBuilder.z42` — backing store for StringWriter
- `src/libraries/z42.io/src/MemoryStream.z42` — pattern reference (read-only-view ctor / growth)
- `src/libraries/z42.io/src/Stream.z42` — convenience helper pattern reference

## Out of Scope

- **`TextReader` / `TextWriter` abstract bases** — premature without a
  third concrete class. When `StreamReader`/`StreamWriter` lands the
  shared shape will be extracted (deferred:
  `io-stream-future-textreader-base`)
- **`StreamReader(Stream, Encoding)` / `StreamWriter(Stream, Encoding)`**
  — requires a real Encoding type; deferred to `add-z42-encoding-stream`
- **`StringReader.ReadBlock(char[], int, int)`** (.NET API that blocks
  until count filled) — convenience; deferrable
- **Async variants** (`ReadLineAsync` etc.) — gated on L3 async/await
- **Custom line terminators** — only `\n` and `\r\n` recognised, matching
  .NET / Python convention

## Open Questions

无 — 纯脚本实现，与 .NET / Python TextReader/Writer 语义一致。
