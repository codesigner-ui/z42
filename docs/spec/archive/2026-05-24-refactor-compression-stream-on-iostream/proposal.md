# Proposal: refactor CompressionStream onto Std.IO.Stream

## Why

[add-z42-io-stream (2026-05-24)](../../archive/2026-05-24-add-z42-io-stream/)
shipped a unified `Std.IO.Stream` + `MemoryStream` base. The
deferred follow-up was to make existing bespoke stream classes
extend it. `Std.Compression.CompressionStream` is the highest-value
target — every future caller wanting "compress this stream of bytes"
(socket / file / pipe) needs the Stream-wrapper shape, and z42 today
forces users into a bespoke `Feed/Finish/Dispose` API that doesn't
compose.

Concrete pipelines this refactor unlocks:

```z42
// Compress straight into a destination stream (file / socket / memory):
MemoryStream dest = new MemoryStream();
Stream encoder = Gzip.WrapWrite(dest);
encoder.Write(plaintext, 0, plaintext.Length);
encoder.Close();
byte[] compressed = dest.ToArray();

// Decompress straight from a source stream:
MemoryStream src = new MemoryStream(compressedBytes);
Stream decoder = Gzip.WrapRead(src);
byte[] plain = decoder.ReadAllBytes();
```

Once `FileStream` / `NetworkStream` land (separate follow-up specs),
these same `WrapWrite / WrapRead` factories accept them transparently:

```z42
// HTTP gzip-encoded response body, streaming:
NetworkStream sock = ...;
Stream gunzipped = Gzip.WrapRead(sock);
TextReader text = new TextReader(gunzipped, Utf8);
string line = text.ReadLine();
```

None of this is writable in z42 today; this refactor is the bridge.

## What Changes

### Replace, don't deprecate

z42 pre-1.0 policy ([`philosophy.md` § 不为旧版本提供兼容](../../../../.claude/rules/philosophy.md))
says **delete the old API** rather than carry deprecation surface.
So:

1. **Remove** `Std.Compression.CompressionStream` (bespoke
   `Feed / Finish / Dispose` shape)
2. **Remove** `Gzip.CompressStream() / DecompressStream()` (and the
   Zlib / Deflate / Zstd counterparts) — they returned the
   bespoke `CompressionStream`
3. **Add** generic internal `_CompressionEncoderStream` and
   `_CompressionDecoderStream` classes that extend `Std.IO.Stream`
   and wrap a destination / source stream respectively
4. **Add** `Gzip.WrapWrite(Stream dest) -> Stream`,
   `Gzip.WrapWrite(Stream dest, int level) -> Stream`,
   `Gzip.WrapRead(Stream src) -> Stream` factory methods (same
   trio on Zlib / Deflate / Zstd)
5. **Keep** existing one-shot helpers unchanged:
   `Gzip.Compress(byte[]) -> byte[]`, `Gzip.Compress(byte[], int) ->
   byte[]`, `Gzip.Decompress(byte[]) -> byte[]`

### Algorithm coverage

All four families covered consistently (no Zstd-only or Gzip-only
gaps):

| Algo | `WrapWrite` level range | `WrapRead` |
|------|-------------------------|------------|
| `Gzip` | 1..=9 (Compression.Default = 6) | ✓ |
| `Zlib` | 1..=9 | ✓ |
| `Deflate` | 1..=9 | ✓ |
| `Zstd` | 1..=22 (Compression.ZstdDefault = 3) | ✓ |

Internal classes are algo-parameterised — one `_CompressionEncoderStream`
+ one `_CompressionDecoderStream` shared across all 4 facades (vs 8
near-duplicate classes).

### Decoder limitation carries over

The decoder side still accumulates input chunks and bulk-decompresses
at `Close` (per [compression.md `compression-future-streaming-decode`
Deferred item](../../../design/stdlib/compression.md#compression-future-streaming-decode)).
`WrapRead(src).Read(buf, off, n)` will return 0 (EOF appearance)
until the user has consumed all source bytes AND called `Close`,
at which point the decoded payload becomes available via a final
`Read` after re-positioning to start. This is subtle — to keep the
v0 semantics intuitive, `_CompressionDecoderStream` instead reads
the entire source upfront on first `Read` call, decompresses
synchronously, and serves bytes out of its internal buffer. Bigger
memory cost but matches user expectations.

Documented constraint: callers reading multi-GB compressed payloads
through `WrapRead` will see the full decompressed output in memory
before any `Read` returns. The `compression-future-streaming-decode`
upgrade lifts this; for v0 use cases (HTTP responses, log entries,
zpkg metadata) it's acceptable.

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.compression/src/CompressionStream.z42` | DELETE | bespoke class retired |
| `src/libraries/z42.compression/src/Gzip.z42` | MODIFY | remove `CompressStream / DecompressStream`; add `WrapWrite(Stream) / WrapWrite(Stream, int) / WrapRead(Stream)` |
| `src/libraries/z42.compression/src/Zlib.z42` | MODIFY | 同 |
| `src/libraries/z42.compression/src/Deflate.z42` | MODIFY | 同 |
| `src/libraries/z42.compression/src/Zstd.z42` | MODIFY | 同 |
| `src/libraries/z42.compression/src/CompressionEncoderStream.z42` | NEW | generic encoder Stream subclass |
| `src/libraries/z42.compression/src/CompressionDecoderStream.z42` | NEW | generic decoder Stream subclass |
| `src/libraries/z42.compression/z42.compression.z42.toml` | MODIFY | add `z42.io` as dep (for `Std.IO.Stream`) |
| `src/libraries/z42.compression/tests/streaming.z42` | MODIFY (RENAME) | replace `Feed/Finish/Dispose` cases with `WrapWrite/WrapRead` pipeline cases |
| `src/libraries/z42.compression/tests/stream_pipeline.z42` | NEW | end-to-end Gzip.WrapWrite(MemoryStream) round-trip |
| `docs/design/stdlib/compression.md` | MODIFY | API section updated; new "Pipeline composition" section; remove old streaming API doc |
| `docs/design/stdlib/io-stream.md` | MODIFY | flip `refactor-compression-stream-on-iostream` Deferred item to "✅ landed 2026-05-24" |
| `docs/design/stdlib/roadmap.md` | MODIFY | mark refactor item completed in Deferred table |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index strike refactor entry |
| `src/libraries/z42.compression/README.md` | MODIFY | example uses new API |

**Read-only references**:

- `src/libraries/z42.io/src/Stream.z42` + `MemoryStream.z42` — base classes
- `src/runtime/crates/z42-compression/src/lib.rs` — cdylib C ABI surface (unchanged)
- `src/runtime/src/native/ext.rs` — ext loader VM-side wrappers (unchanged)

## Out of Scope

- True streaming-decode upgrade (still `compression-future-
  streaming-decode` follow-up)
- Refactor `BinaryReader/Writer` to accept Stream (separate
  `refactor-binary-reader-stream` follow-up)
- `FileStream` / `NetworkStream` integration tests (those streams
  don't exist yet — own specs)
- `Brotli` / `Xz` / `Lz4` (algorithm-coverage follow-ups; if/when
  they land they'll use the same generic `_CompressionEncoderStream`
  / `_CompressionDecoderStream` shape automatically)
- Async variants (gated on L3 async/await)

## Open Questions

- [ ] **Default level on `WrapWrite(Stream)` no-args**: `Gzip` uses
  `Compression.Default=6`; `Zstd` uses `Compression.ZstdDefault=3`.
  Confirm: facade overload picks the per-algo default automatically.
- [ ] **Close cascade**: when the encoder Stream's `Close()` is
  called, should it also `Close()` the wrapped destination Stream?
  Lean **no** — caller owns dest's lifecycle (matches .NET
  `GZipStream(dest, mode, leaveOpen: true)` non-default behaviour but
  z42 v0 always leaves dest open; safer default for pipeline
  composition).
- [ ] **Multiple-pass through cdylib slot table**: `Write(buf, off,
  n)` calls `__compressor_feed` once per Write; that's one cdylib
  round-trip per call. Acceptable for typical buffer sizes (≥ 4 KB)
  but could be hot on tiny Writes. Defer perf concern; users who care
  can interpose `BufferedStream` once it lands.
