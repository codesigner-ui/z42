# z42.compression

Gzip / Zlib / Deflate / Zstandard primitives + Tar / Zip archive
parsing. First stdlib package whose native code lives **outside**
z42vm — built as a separate `cdylib` (`libz42_compression.{so,dylib,
dll}`) loaded via the native ext loader.

## One-shot

```z42
using Std.Compression;

byte[] compressed   = Gzip.Compress(original);          // default level 6
byte[] decompressed = Gzip.Decompress(compressed);
byte[] best         = Gzip.Compress(original, 9);

byte[] zstdEnc = Zstd.Compress(original);                // default level 3
byte[] zstdDec = Zstd.Decompress(zstdEnc);
```

## Pipeline (Stream-based, refactor 2026-05-24)

```z42
using Std.IO;
using Std.Compression;

// Compress straight into a destination Stream:
MemoryStream dest = new MemoryStream();
Stream enc = Gzip.WrapWrite(dest);          // default level
enc.Write(plaintext, 0, plaintext.Length);
enc.Close();                                 // emits gzip footer
byte[] compressed = dest.ToArray();

// Decompress straight from a source Stream:
MemoryStream src = new MemoryStream(compressedBytes);
Stream dec = Gzip.WrapRead(src);
byte[] plain = dec.ReadAllBytes();
```

Same `WrapWrite / WrapRead` shape on `Zlib`, `Deflate`, `Zstd`. Future
`FileStream` / `NetworkStream` slot in transparently — no API change.

## Archive

```z42
using Std.Archive;

// Read existing zip:
ZipEntry[] entries = Zip.Read(zipBytes);
byte[] hello = Zip.ExtractFile(zipBytes, "hello.txt");

// Tar write (Zip write deferred — see compression.md):
TarEntry[] entries = new TarEntry[] {
    new TarEntry("readme.txt", contentBytes, 0644),
};
byte[] tarBytes = Tar.Write(entries);
```

See [docs/design/stdlib/compression.md](../../../docs/design/stdlib/compression.md)
for the full API + Deferred items.
