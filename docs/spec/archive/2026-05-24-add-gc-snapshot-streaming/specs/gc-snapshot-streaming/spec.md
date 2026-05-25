# Spec: GC Heap Snapshot — Streaming Serializer

## ADDED Requirements

### Requirement: `serialize_v8_heapsnapshot_to` writes JSON directly to `impl Write`

#### Scenario: Returns byte count of data written
- **WHEN** `serialize_v8_heapsnapshot_to(&snap, &mut writer)` is
  called for any non-empty snapshot
- **THEN** returns `Ok(n)` where `n` equals the number of bytes
  written to `writer`
- **AND** all `Write::write_all` calls succeed (any failure is
  propagated up the `io::Result`)

#### Scenario: Output is byte-identical to in-memory version
- **GIVEN** a fixed `GraphSnapshot`
- **WHEN** `serialize_v8_heapsnapshot(&snap)` returns a `String`
- **AND** `serialize_v8_heapsnapshot_to(&snap, &mut buf)` writes to
  a `Vec<u8>`
- **THEN** the byte sequences are exactly equal
- **AND** the returned `u64` matches `String::len()`

#### Scenario: Writes to a buffered file writer end-to-end
- **WHEN** `serialize_v8_heapsnapshot_to(&snap, &mut BufWriter::new(File))`
  is called and the writer flushed
- **THEN** the file's content (read back as UTF-8) matches the
  in-memory serializer's output

### Requirement: `Std.GC.WriteHeapSnapshot` uses streaming path

#### Scenario: builtin opens BufWriter and avoids intermediate String
- **WHEN** the z42 builtin `__gc_write_heap_snapshot` runs
- **THEN** it opens the destination file with `File::create` +
  `BufWriter::new`
- **AND** calls `serialize_v8_heapsnapshot_to` (not the String
  variant)
- **AND** flushes the writer before returning success
- **AND** propagates any io error as an `anyhow` error

#### Scenario: Returned byte count still matches file length
- **WHEN** `Std.GC.WriteHeapSnapshot(path)` returns `n_bytes`
- **THEN** reading `path` back yields exactly `n_bytes` bytes of
  content (assertion already verified by the B3 e2e test
  `test_returned_bytes_equal_file_length`)

## MODIFIED Requirements

### Requirement: `serialize_v8_heapsnapshot(&GraphSnapshot) -> String`

**Before**: builds full `String` inline; returns it directly.

**After**: thin wrapper that drives a `Vec<u8>` `Write` sink to
the streaming serializer, then `String::from_utf8_unchecked`s the
bytes. Existing callers (unit tests + `escape_json_str` shared
helper) see no behavioural change — output stays byte-identical.

### Requirement: `escape_json_str`

**Before**: `fn escape_json_str(s: &str, out: &mut String)`.

**After**: `fn escape_json_str_to<W: Write>(s: &str, writer: &mut W)
-> io::Result<u64>`. Single source of truth for JSON-escape rules
(both streaming and in-memory paths drive it). The old `&mut String`
variant is removed; the in-memory wrapper feeds `Vec<u8>` (which
implements `Write`) so no String-flavoured API is needed.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变（dispatch table 走 existing builtin）
- [x] JIT — 不变
- [x] GC subsystem — `gc/snapshot.rs` adds streaming serializer +
  retrofits `escape_json_str` to `impl Write`
- [x] corelib — `builtin_gc_write_heap_snapshot` switches to
  `BufWriter<File>` + streaming
- [x] stdlib — 不变（builtin 名 / 返回类型不变）

## IR Mapping

无新 IR 指令。
