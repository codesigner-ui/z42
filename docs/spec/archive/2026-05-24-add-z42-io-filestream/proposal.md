# Proposal: add Std.IO.FileStream

## Why

[add-z42-io-stream (2026-05-24)](../../archive/2026-05-24-add-z42-io-stream/)
shipped `Std.IO.Stream` + `MemoryStream`. Follow-ups landed same day:
[refactor-compression-stream-on-iostream](../../archive/2026-05-24-refactor-compression-stream-on-iostream/)
+ [refactor-binary-reader-stream](../../archive/2026-05-24-refactor-binary-reader-stream/).

The composability story so far works in-memory only — `Gzip.WrapRead
(MemoryStream(bytes))`, `BinaryReader(MemoryStream(bytes))` etc. The
gap: **file-backed streams**. Today users wanting to read a structured
binary file or process a `tar.gz` log must call `File.ReadAllBytes(path)`
first (loading the entire file into memory) before any of the new
stream APIs apply. That's fine for small configs / metadata; it's
wasteful for hundred-MB log files and impossible for multi-GB
archives.

`FileStream : Stream` closes the gap:

```z42
// Streaming gzip-compressed log line-by-line (with future TextReader):
FileStream f = new FileStream("logs/app.log.gz");
Stream gz = Gzip.WrapRead(f);
// reads bytes off disk in 4 KB chunks, decompresses on the fly,
// without ever buffering the whole file

// Or just structured binary from a large file:
FileStream f = new FileStream("data.bin");
BinaryReader r = new BinaryReader(f);
int magic = r.ReadInt32LE();          // 4 bytes off disk, done
```

## What Changes

### New VM builtins (in-VM corelib, follow existing fs.rs pattern)

Eight file-handle builtins, following the same slot-table model as
`ProcessSlot` / `ChannelSlot` / `MutexSlot`:

| Builtin | Args | Returns |
|---------|------|---------|
| `__file_open(path, mode) -> slot_id` | `path: string, mode: int (0=Read 1=Write 2=Append)` | `long` slot id |
| `__file_read(slot, buf, off, count) -> n` | slot + Array<I64-as-u8> + ints | `int` bytes read (0 = EOF) |
| `__file_write(slot, buf, off, count)` | same shape | `null` |
| `__file_seek(slot, offset, origin) -> new_pos` | `long offset, int origin` | `long` new absolute pos |
| `__file_length(slot) -> long` | slot | `long` total file size |
| `__file_position(slot) -> long` | slot | `long` cursor |
| `__file_flush(slot)` | slot | `null` |
| `__file_close(slot)` | slot | `null` (idempotent) |

### VmCore.file_handles slot table

```rust
// vm_context.rs
pub(crate) file_handles: Mutex<HashMap<u64, FileHandleSlot>>;
pub(crate) next_file_handle_id: AtomicU64;

// corelib/fs.rs
pub(crate) struct FileHandleSlot {
    pub(crate) file: Option<std::fs::File>,   // None after close
    pub(crate) mode: FileMode,
}
```

### z42 facade

```z42
namespace Std.IO;

public static class FileMode {
    public static int Read   = 0;   // open existing, read-only
    public static int Write  = 1;   // create / truncate, write-only
    public static int Append = 2;   // create-if-missing / append, write-only
}

public sealed class FileStream : Stream {
    public FileStream(string path);                  // Read mode (default)
    public FileStream(string path, int mode);        // explicit FileMode

    // Stream overrides ↓
    public override bool CanRead();
    public override bool CanWrite();
    public override bool CanSeek();   // true for Read+Write, false for Append
    public override int  Read(byte[], int, int);
    public override void Write(byte[], int, int);
    public override long Length();
    public override long Position();
    public override long Seek(long offset, int origin);
    public override void Flush();
    public override void Close();
}
```

`Close()` releases the OS file handle. `Dispose()` future when z42
gets `using` syntax; for v0 callers explicitly `Close()`.

## Scope

| File | Type | Note |
|------|------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | new `file_handles: Mutex<HashMap<u64, FileHandleSlot>>` + `next_file_handle_id: AtomicU64` fields on VmCore |
| `src/runtime/src/corelib/fs.rs` | MODIFY | NEW `FileHandleSlot` struct + 8 builtin fns (`builtin_file_open` / `_read` / `_write` / `_seek` / `_length` / `_position` / `_flush` / `_close`) |
| `src/runtime/src/corelib/mod.rs` | MODIFY | register 8 new builtins (append at end, preserve BuiltinId stability) |
| `src/libraries/z42.io/src/FileMode.z42` | NEW | `static int Read=0 / Write=1 / Append=2` |
| `src/libraries/z42.io/src/FileStream.z42` | NEW | `FileStream : Stream` z42 facade |
| `src/libraries/z42.io/tests/file_stream.z42` | NEW | open/read/write/append/seek/close + round-trip + pipeline composition with Gzip / BinaryReader |
| `docs/design/stdlib/io-stream.md` | MODIFY | flip `io-stream-future-filestream` Deferred to "✅ landed" |
| `docs/design/stdlib/io-file.md` (if exists; else inline z42.io overview) | MODIFY | add FileStream section |
| `docs/design/stdlib/roadmap.md` | MODIFY | mark FileStream landed in Stream Deferred index |
| `docs/roadmap.md` | MODIFY | strike row |

**Read-only references**:

- `src/runtime/src/corelib/process.rs` — slot-table pattern reference
- `src/libraries/z42.compression/src/CompressionEncoderStream.z42` — z42-side Stream subclass reference
- `src/libraries/z42.io/src/Stream.z42` + `MemoryStream.z42` — base contract

## Out of Scope

- `FileMode.ReadWrite` (open existing for both read+write) — separate
  `add-z42-io-filestream-readwrite` follow-up; v0 picks the 3 most
  common modes
- File locking (`flock` / Windows `LockFileEx`) — own spec
- File metadata (Length / mtime / permissions) beyond what
  Stream.Length() exposes — `add-z42-io-fileinfo` follow-up
- Atomic write (tmp file + rename) — `add-z42-io-atomic-write`
  follow-up
- Async file I/O — gated on L3 async/await
- `FileStream(handle: int)` constructor from a raw OS fd — niche

## Open Questions

- [ ] **`CanSeek` for append mode**: POSIX append-mode opens with
  `O_APPEND` which makes seek a no-op on writes (writes always go to
  EOF). v0 returns `CanSeek() == false` in Append mode to be honest
  about that semantic; reads via `lseek` still work but z42 Append
  mode is write-only anyway.
- [ ] **Path encoding**: paths are z42 `string` (UTF-8 → bytes).
  Some platforms (older Windows) have non-UTF-8 path conventions.
  v0: pass as UTF-8 always; document that paths with non-UTF-8
  bytes are out of scope.
- [ ] **Truncation on Write mode**: matches existing
  `File.WriteAllText(path, text)` which is "create-or-truncate"
  — same semantic.
