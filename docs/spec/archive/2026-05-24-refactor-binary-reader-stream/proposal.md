# Proposal: refactor BinaryReader / BinaryWriter onto Std.IO.Stream

## Why

[add-z42-io-stream (2026-05-24)](../../archive/2026-05-24-add-z42-io-stream/)
shipped `Std.IO.Stream` + `MemoryStream`. The first adopter was
`Std.Compression.CompressionStream` ([refactored 2026-05-24](../../archive/2026-05-24-refactor-compression-stream-on-iostream/));
this spec is the second.

Today `Std.IO.Binary.BinaryReader` and `BinaryWriter` only accept
`byte[]`. That forces callers wanting to read structured binary data
from a file / socket / decompressed stream to first load the entire
payload into memory:

```z42
// Pre-refactor: must materialise full file first
byte[] all = File.ReadAllBytes("magic.bin");           // could be GB-sized
BinaryReader r = new BinaryReader(all);
int magic = r.ReadInt32LE();
// ... 4 bytes used, 999 MB wasted
```

Post-refactor (`FileStream` / `NetworkStream` land in their own
follow-up specs):

```z42
FileStream f = new FileStream("magic.bin");
BinaryReader r = new BinaryReader(f);   // streams from disk
int magic = r.ReadInt32LE();             // 4 bytes read, that's it
```

Equally important: `BinaryReader` can now consume the output of
`Gzip.WrapRead(...)` directly — structured binary parsing through a
decompression pipeline composes naturally:

```z42
Stream gz = Gzip.WrapRead(new MemoryStream(compressedBytes));
BinaryReader r = new BinaryReader(gz);
int magic = r.ReadInt32LE();
```

## What Changes

### Replace, don't deprecate (pre-1.0 policy)

z42's [philosophy.md "不为旧版本提供兼容"](../../../../.claude/rules/philosophy.md)
says delete-and-replace rather than carry deprecation surface. BUT
in this case the existing `byte[]` constructor is **convenient
sugar that doesn't add cost** — internally we delegate to a
`new MemoryStream(data)` wrapper. So both constructors stay:

| Constructor | Internal storage |
|-------------|------------------|
| `BinaryReader(byte[] data)` (existing) | `new MemoryStream(data)` (read-only view) |
| `BinaryReader(Stream src)` (NEW) | `src` directly |
| `BinaryWriter()` (existing) | new growable `MemoryStream` |
| `BinaryWriter(int initialCapacity)` (existing) | new growable `MemoryStream` (capacity-hinted) |
| `BinaryWriter(Stream dest)` (NEW) | `dest` directly |

### Capability-dependent operations

The position-tracking API (`GetPosition`, `GetLength`, `Seek`,
`Skip`, `EndOfStream`) currently treats the underlying `byte[]` as
seekable. After refactor:

- **Seekable underlying Stream** (`MemoryStream`, future `FileStream`):
  all position ops work as before.
- **Non-seekable underlying Stream** (future `NetworkStream`,
  pipe-style streams, `CompressionDecoderStream` already): position
  ops throw `NotSupportedException` from the underlying Stream; the
  read methods themselves keep working.

Existing `Std.IO.Binary.BinaryException` is the right error type for
the user-facing "you tried to seek a non-seekable stream"
diagnostic — wrap the underlying `NotSupportedException` with a
`BinaryException` that mentions the binary operation that failed.

### `BinaryWriter.ToArray() / Clear()` only for default constructors

These two methods only make sense when the writer owns the destination
buffer (i.e. internally-created `MemoryStream`). When the user
supplies a `Stream dest`, `ToArray()` and `Clear()` throw
`BinaryException("ToArray / Clear only valid for default constructor
(internal MemoryStream backing)")`.

Implementation: track a `_ownsStream: bool` flag set true by the
default constructors, false by the `Stream`-accepting one. Methods
gate on the flag.

## Scope

| File | Type | Note |
|------|------|------|
| `src/libraries/z42.io.binary/z42.io.binary.z42.toml` | MODIFY | add `z42.io` dep |
| `src/libraries/z42.io.binary/src/BinaryReader.z42` | MODIFY | internal storage `byte[] _data + int _pos` → `Stream _stream` (delegates to MemoryStream for the byte[] constructor path); add `Stream`-accepting constructor; capability-gated `Seek` / `Skip` / `EndOfStream` / `GetPosition` / `GetLength` |
| `src/libraries/z42.io.binary/src/BinaryWriter.z42` | MODIFY | internal `byte[] _buffer + int _pos` → `Stream _stream` + `_ownsStream: bool`; add `Stream`-accepting constructor; `ToArray() / Clear()` gated on `_ownsStream` |
| `src/libraries/z42.io.binary/tests/binary_basic.z42` | (UNCHANGED) | existing byte[] constructor path still works |
| `src/libraries/z42.io.binary/tests/binary_errors.z42` | (UNCHANGED) | same |
| `src/libraries/z42.io.binary/tests/binary_strings.z42` | (UNCHANGED) | same |
| `src/libraries/z42.io.binary/tests/binary_stream.z42` | NEW | Stream-source / dest-Stream pipeline cases, pipeline composition with MemoryStream (and CompressionDecoderStream when available) |
| `docs/design/stdlib/io-binary.md` | MODIFY | API section: add Stream-constructor row; new "Pipeline composition" section |
| `docs/design/stdlib/io-stream.md` | MODIFY | flip `refactor-binary-reader-stream` Deferred to "✅ landed" |
| `docs/design/stdlib/roadmap.md` | MODIFY | mark refactor done in Stream 延后项索引 |
| `docs/roadmap.md` | MODIFY | strike refactor row |
| `src/libraries/z42.io.binary/README.md` (if exists) or NEW | NEW | update example |

**Read-only references**:

- `src/libraries/z42.io/src/Stream.z42` + `MemoryStream.z42` — base
- `docs/spec/archive/2026-05-24-refactor-compression-stream-on-iostream/` — mirror

## Out of Scope

- `FileStream` integration tests (FileStream doesn't exist yet —
  own spec)
- `NetworkStream` integration tests (own spec under add-z42-net)
- New Read / Write methods (BigEndian variants etc.) — separate
  spec if needed; existing surface stays
- Async variants — gated on L3 async/await

## Open Questions

- [ ] **Position type**: existing `GetPosition() / GetLength()`
  return `int`. Underlying `Stream` returns `long`. For seekable
  streams ≥ 2 GB we'd truncate. v0 keeps `int` (matches existing
  API; truncation acceptable for v0 byte[]-flavor callers). Future
  `add-z42-io-binary-long-position` spec adds `GetPositionLong() /
  GetLengthLong()` if real use case appears.
- [ ] **Should non-seekable streams allow `ReadByte()` to still work
  even though `GetPosition()` throws?** Yes — the underlying
  Stream's `Read(buffer, 0, 1)` works on every readable Stream
  regardless of seekability. Position tracking is independent.
- [ ] **Close-cascade**: `BinaryReader` / `Writer` don't currently
  expose `Close()`. Should we add one that closes the underlying
  Stream? Lean **no** — caller owns the Stream lifecycle (same
  precedent as `CompressionEncoderStream / DecoderStream` from the
  previous refactor).
