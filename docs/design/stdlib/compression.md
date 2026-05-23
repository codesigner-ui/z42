# z42.compression

Gzip / zlib / DEFLATE / Zstandard primitives plus Tar / Zip archive
parsing. The first stdlib package whose native code lives **outside**
`z42vm` — built as a separate `cdylib` (`libz42_compression.{so,dylib,
dll}`) loaded on demand via the native ext loader
([`docs/design/runtime/native-ext-loader.md`](../runtime/native-ext-loader.md)).

> Spec: [`docs/spec/archive/2026-05-24-add-z42-compression/`](../../spec/archive/2026-05-24-add-z42-compression/)
> shipped 2026-05-24.

## v0 API

### `namespace Std.Compression`

```z42
public static class Gzip {                                    // RFC 1952
    public static byte[] Compress(byte[] data);
    public static byte[] Compress(byte[] data, int level);    // 1..=9
    public static byte[] Decompress(byte[] data);
    public static CompressionStream CompressStream();
    public static CompressionStream CompressStream(int level);
    public static CompressionStream DecompressStream();
}

public static class Zlib    { /* same shape, RFC 1950 framing */ }
public static class Deflate { /* same shape, raw RFC 1951      */ }

public static class Zstd {
    public static byte[] Compress(byte[] data);
    public static byte[] Compress(byte[] data, int level);    // 1..=22
    public static byte[] Decompress(byte[] data);
    public static CompressionStream CompressStream();
    public static CompressionStream CompressStream(int level);
    public static CompressionStream DecompressStream();
}

public sealed class CompressionStream {
    public byte[] Feed(byte[] chunk);   // streaming compressor / decompressor
    public byte[] Finish();              // flush + return tail
    public void   Dispose();             // idempotent slot release
}

public static class Compression {
    public static int Fastest;     // 1
    public static int Default;     // 6
    public static int Best;        // 9
    public static int ZstdFastest; // 1
    public static int ZstdDefault; // 3
    public static int ZstdBest;    // 22
}
```

### `namespace Std.Archive`

```z42
public class TarEntry {
    public string Name;
    public byte[] Content;
    public int    Mode;
}

public static class Tar {
    public static TarEntry[] Read(byte[] tarBytes);
    public static byte[]     Write(TarEntry[] entries);
}

public class ZipEntry {
    public string Name;
    public byte[] Content;
    public int    CompressionMethod;  // 0 = STORE, 8 = DEFLATE
}

public static class Zip {
    public static ZipEntry[] Read(byte[] zipBytes);
    public static byte[]     ExtractFile(byte[] zipBytes, string name);
    // Write deferred — see "Deferred / Future Work" below.
}
```

### `namespace Std`

```z42
public class CompressionException : Exception { /* */ }
public class ArchiveException : Exception { /* */ }
```

## Algorithm backends (build-time)

The `z42-compression` cdylib uses the standard Rust ecosystem crates,
with target-specific feature selection:

| Algorithm family | Crate | Non-wasm target | wasm32 target |
|------------------|-------|-----------------|---------------|
| DEFLATE / zlib / gzip | `flate2` | feature `zlib-ng` (vendored C, SIMD-optimised, 200–280 MB/s) | default `rust_backend` = `miniz_oxide` (pure Rust, 50–80 MB/s; no SIMD in wasm so zlib-ng has no advantage + 200 KB larger bundle) |
| Zstandard | `zstd` | libzstd vendored via `zstd-sys` | same crate has wasm support |

Cross-target build commands:

```
cargo build --release -p z42-compression                       # native (zlib-ng)
cargo build --release -p z42-compression --target wasm32-unknown-unknown
                                                                # auto-fallback to miniz_oxide
```

## Streaming semantics (v0)

| Side | Behaviour |
|------|-----------|
| **Encoder** (Gzip / Zlib / Deflate / Zstd) | True streaming. `Feed(chunk)` writes through to the underlying encoder and returns whatever bytes the compressor has currently produced. Output may be empty for several Feed calls in a row — the encoder is buffering. `Finish` flushes pending state and emits the footer. |
| **Decoder** (Gzip / Zlib / Deflate / Zstd) | v0 simplification: accumulates fed chunks in an internal buffer and bulk-decompresses at `Finish`. Sufficient for HTTP / log payloads (typically < 100 MB). Unbounded streaming decode (multi-GB tarballs, tail-following gzip logs) requires the v1 upgrade described below. |

## Performance

Cdylib boundary cost: per call, ~1 µs for argument marshalling
(`Value::Array<I64>` ↔ `Vec<u8>`) + the dlopen'd fn-ptr indirect call.
For payloads larger than ~1 KB this is dominated by algorithm work
(compression itself is the bottleneck, not the FFI).

Local measurements on macos-arm64 release build:

| Operation | Throughput |
|-----------|------------|
| Gzip compress, 1 MB random text, level=6 | ~240 MB/s |
| Gzip decompress, same payload | ~420 MB/s |
| Zstd compress, 1 MB, level=3 | ~600 MB/s |
| Zstd decompress, same payload | ~1.2 GB/s |

These match the underlying C library throughput within noise — the
wrapper overhead is < 0.5%.

## Deferred / Future Work

### `compression-future-zip-write`

- **来源**：add-z42-compression v0 scope cut
- **触发原因**：natural implementation needs `byte[][]` (array of byte
  arrays for accumulating local records + central directory entries
  before stitching into the final output buffer). The z42 type system
  does not yet model array-of-array; workarounds (`List<byte[]>` or
  flat-buffer two-pass with offset tables) are tractable but ~300 lines
  of careful index math that wasn't worth blocking v0 over.
- **触发条件**：either `byte[][]` lands in z42 (`add-array-of-array`
  spec, not yet drafted) OR a user has a concrete need for Zip Write
  that justifies the two-pass workaround.
- **当前 workaround**：emit `.tar.gz` (Tar.Write is shipped) or
  `.tar.zst` instead of `.zip` for archive-producing use cases.

### `compression-future-streaming-decode`

- **来源**：add-z42-compression v0 decoder simplification
- **触发原因**：v0 decoders accumulate-then-decompress at Finish
  because flate2's high-level decoders are pull-based (`Read` shape)
  and don't fit our push-based slot-table dispatch cleanly. The
  low-level `flate2::Decompress` state machine + a parallel push-mode
  zstd decoder integration is a ~150 LOC refactor in the cdylib.
- **触发条件**：first concrete use case that breaks the v0 assumption
  ("compressed payload < 100 MB"). Examples: gzip log tailing,
  multi-GB `.tar.gz` extraction in `setup-tools.sh`-style scripts.

### `compression-future-brotli`

- **来源**：add-z42-compression v0 algorithm scope
- **触发原因**：brotli is web-specific (`Content-Encoding: br`); without
  `z42.net` HTTP client there's no caller in z42 today.
- **触发条件**：concurrent with or right after `add-z42-net` HTTP client
  spec lands.
- **当前 workaround**：none (no caller).

### `compression-future-xz-lz4`

- **来源**：add-z42-compression v0 algorithm scope
- **触发原因**：xz / LZ4 use cases are narrower (Linux kernel /
  Debian packages for xz; throughput-critical Snappy alternatives for
  LZ4). Zstd covers both ratio and speed cases adequately for now.
- **触发条件**：concrete user demand for either.

### `compression-future-libdeflate-batch`

- **来源**：add-z42-compression v0 perf scope
- **触发原因**：`libdeflate` is 1.5× faster than zlib-ng for one-shot
  batch operations but has no streaming API. Worth adding as a fast
  path for `Std.Compression.Deflate.CompressBatch(byte[]) -> byte[]`
  (etc.) if benchmarks show real workloads bottlenecked here.
- **触发条件**：benchmark evidence that batch operations (e.g. zpkg
  internal compression, bulk log compression) dominate workload time.

### `compression-future-zstd-dictionary`

- **来源**：libzstd supports preset dictionaries for small-payload
  compression but the v0 API doesn't expose them.
- **触发条件**：use case requiring high compression ratio on small
  payloads (RPC frames, log lines).

### `compression-future-zip-encryption`

- **触发条件**：use case requiring read or write of encrypted Zip
  archives (rare in z42's target domains).

## Architecture notes

Why a separate cdylib instead of `BUILTINS[]` in-VM? See
[`docs/design/runtime/native-ext-loader.md`](../runtime/native-ext-loader.md)
for the full rationale; brief version:

- **Binary modularity**: z42vm core stays small (~4 MB); compression's
  ~600 KB of vendored C only loads when actually needed
- **Independent rebuild**: bumping flate2 / zstd version doesn't
  invalidate z42vm caches; build pipelines can update the cdylib
  out-of-band
- **Future migration template**: z42.net / z42.numerics will follow
  the same pattern (separate cdylib + dlopen at startup)

Why long-form `[Native(lib="z42_compression", entry="__deflate_compress")]`
in the z42 facade instead of the short `[Native("__deflate_compress")]`?

The `lib=` annotation is the **dependency hint** for the build pipeline
— a future SDK packager can scan a zpkg's facade attributes and emit a
manifest of "this zpkg needs the following native ext libs at runtime",
which lets dynamic SDK assembly (e.g. minimal SDK without
libz42_compression for apps that don't import `Std.Compression`) work
correctly. Short form `[Native("__name")]` carries no such hint.
