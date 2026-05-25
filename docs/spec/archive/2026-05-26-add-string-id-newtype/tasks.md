# Tasks: add StringId newtype (foundation for Part 5 P0 string interning)

> 状态：🟢 已完成 | 创建：2026-05-26 | 完成：2026-05-26 | 模式：minimal-mode (3 文件, refactor 类)

**变更说明**：Phase A landing of Part 5 P0 string-interning refactor. Adds
`StringId(u32)` newtype + lookup helpers around the existing
`Module.string_pool: Vec<String>`. Zero consumer migrations — future
commits replace individual `String` fields with `StringId` one at a time.

**原因**：docs/review.md Part 5 P0. z42 data structures are bloated with
inline `String` fields (TypeDesc ≈ 336B, FieldSlot 48B, Instruction up
to 100B+) most of which carry names / type tags / method names that
duplicate strings already present in `Module.string_pool`. CoreCLR /
Roslyn parallel: metadata token tables. Migrating to `StringId(u32)`
typically saves 20B per field (Rust `String` 24B → `StringId` 4B) and
makes hash lookups u32-keyed instead of string-keyed.

**Phase A scope (this commit)** — infrastructure only:
- `StringId(u32)` newtype with `INVALID` sentinel and ergonomic accessors
- Lookup helpers on `&Module` (`get_string(id)`, `intern(s)`) and
  `&LazyLoader.string_pool` (same shape)
- Documentation listing future migration targets

**Phase B+ (separate commits, one per migration target)**:
- `Function.name` / `Function.ret_type` / `Function.param_types` / etc.
- `TypeDesc.name` / `FieldSlot.name` / `FieldSlot.type_tag`
- `Instruction` variants with String fields (`Call.func`, `Builtin.name`,
  `FieldGet.field_name`, etc.) — biggest win because each instruction is
  on the hot path and large variants drag the whole enum size

**文档影响**：
- `docs/review.md` Part 5 P0 status: ❌ → 🟡 Phase A done

## Tasks

- [x] 1.1 `src/runtime/src/metadata/string_id.rs` NEW (~210 LOC) — `StringId(u32)` `#[repr(transparent)]` + `INVALID` const + From/Into u32 + Display + `resolve(&pool) -> Option<&str>` + `resolve_unwrap(&pool) -> &str` + **11 unit tests** (含 size_is_four_bytes 验证)
- [x] 1.2 `src/runtime/src/metadata/mod.rs` MODIFY — `pub mod string_id;` + `pub use string_id::StringId;` re-export
- [x] 1.3 Build green; 704/704 lib tests (was 693 + 11 new)

## Design notes

- `StringId(u32)` — newtype around the index into `Module.string_pool` (or
  `LazyLoader.string_pool` after `main_pool_len` offset). 4 bytes per ID
  vs 24 bytes per `String`, with O(1) `Vec` index dereferencing.
- `StringId::INVALID = StringId(u32::MAX)` — sentinel for "no string"
  (matches `tokens::UNRESOLVED` pattern already used by TypeId / MethodId).
- Phase A intentionally does NOT add interning behavior (looking up an
  existing string by content and returning its ID). That's a Phase B+
  feature when consumers actually need it; current zbc loading just
  preserves IDs from the wire format.
- No serde implementation in Phase A — `StringId` is a runtime-only type;
  the wire format already stores u32 indices, no transformation needed.

## Why minimal-mode

- 3 files (new + module decl + tasks.md)
- No public API breakage (purely additive)
- No wire format change
- No semantic change
