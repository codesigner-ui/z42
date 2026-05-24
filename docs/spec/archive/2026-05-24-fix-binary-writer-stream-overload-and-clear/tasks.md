# Tasks: fix BinaryReader / BinaryWriter Stream-vs-byte[] overload-resolution + Clear semantics

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：fix (10 pre-existing test failures + 1 stdlib API ergonomic)

**变更说明**：Three related fixes in `z42.io.binary`:

1. **BinaryWriter(int initialCapacity) ctor dropped**. It was already
   an API-compat no-op (the MemoryStream backing picks its own initial
   cap and grows on demand). With it present alongside `BinaryWriter(Stream)`,
   z42's overload resolution picked the int variant when given a
   `MemoryStream` and bailed with "type mismatch in comparison: Object
   vs I64" inside `initialCapacity < 0`.

2. **BinaryReader(Stream) → `BinaryReader.OverStream(Stream)` static
   factory**. Same z42 overload-resolution shape — when both
   `BinaryReader(byte[])` and `BinaryReader(Stream)` were normal ctors,
   z42 always picked the FIRST-declared one regardless of arg type,
   breaking whichever the second one was. Mirroring on the writer side
   (even though `BinaryWriter` only has `()` + `(Stream)` post-fix-1
   and no current ambiguity) so the two classes have aligned API
   shapes and future expansion doesn't re-introduce the bug.

3. **BinaryWriter.Clear() semantics**: was `Seek(0)` which left the
   existing bytes in place (so `GetLength` still reported the old
   total and a follow-up Write returned a mixed buffer). Now
   re-initialises the MemoryStream, matching .NET behaviour and
   matching the existing `test_writer_clear_resets_position`
   expectation (`Assert.Equal(0, w.GetLength())` post-Clear).

**Underlying z42 compiler bug (not fixed here)**: overload resolution
picks the first-declared candidate regardless of arg type when
multiple arity-1 signatures exist. Same shape bit several previous
specs (StringWriter dropped `Write(char)`, JSON/TOML/YAML use
`ParseStream` instead of overloaded `Parse(Stream)`). When the
compiler is fixed, the static factories here can be folded back into
ctors. Tracked separately.

**文档影响**：z42.io.binary/README.md updated with new factory API.

## Tasks

- [x] 1.1 MODIFY `src/libraries/z42.io.binary/src/BinaryWriter.z42`:
      - drop `BinaryWriter(int initialCapacity)` ctor
      - drop `BinaryWriter(Stream dest)` ctor, replace with static
        `OverStream(Stream)` factory
      - `Clear()` re-inits MemoryStream (was Seek(0))
- [x] 1.2 MODIFY `src/libraries/z42.io.binary/src/BinaryReader.z42`:
      - drop `BinaryReader(Stream src)` ctor, replace with static
        `OverStream(Stream)` factory
      - keep `BinaryReader(byte[] data)` as primary ctor
- [x] 1.3 MODIFY `src/libraries/z42.io.binary/tests/binary_stream.z42`:
      - all `new BinaryWriter(stream)` → `BinaryWriter.OverStream(stream)`
      - all `new BinaryReader(stream)` → `BinaryReader.OverStream(stream)`
- [x] 1.4 MODIFY `src/libraries/z42.io.binary/tests/binary_errors.z42`:
      - `new BinaryWriter(2)` → `new BinaryWriter()` (int ctor gone)
- [x] 1.5 MODIFY `src/libraries/z42.io.binary/README.md` — document
      new factory API
- [x] 2.1 35 binary tests now pass (was 10 failing on main)
- [x] 3.1 Archive + commit + push
