//! `StringId(u32)` — typed index into a string pool.
//!
//! Part 5 P0 Phase A foundation (docs/spec/changes/add-string-id-newtype/,
//! 2026-05-26). Wraps the existing `Module.string_pool: Vec<String>` so
//! callers get a typed handle rather than passing raw `u32` indices around
//! (loses type safety, no IDE jump-to-def) or storing inline `String`
//! fields (wastes 20 bytes/field plus heap allocation cost).
//!
//! # Phase A
//!
//! This module ships only the type + lookup helpers. **No production
//! consumers are migrated to `StringId` yet.** Future Phase B commits
//! replace individual `String` fields (e.g. `Function.name`,
//! `TypeDesc.name`, `Instruction::Call::func`) one at a time.
//!
//! # Sentinel
//!
//! [`StringId::INVALID`] = `StringId(u32::MAX)` mirrors the
//! [`crate::metadata::tokens::UNRESOLVED`] convention. Callers can use it
//! as "no string" without `Option<StringId>` boxing (saves 4 bytes per
//! slot in dense tables).
//!
//! # Wire format
//!
//! The zbc binary format already stores string-pool indices as `u32` —
//! `StringId` is a runtime-only typed view; no serde implementation
//! needed in Phase A. Phase B+ may add `#[serde(transparent)]` when
//! migrating bytecode-serialized fields.

use std::fmt;

/// Typed index into a string pool (either `Module.string_pool` for
/// intra-module references, or `LazyLoader.string_pool` for cross-zpkg
/// references after the `main_pool_len` offset adjustment).
///
/// `#[repr(transparent)]` so the type carries zero runtime overhead and
/// FFI-friendly layout matches a raw `u32`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
#[repr(transparent)]
pub struct StringId(pub u32);

impl StringId {
    /// Sentinel for "no string". Pattern mirrors
    /// [`crate::metadata::tokens::UNRESOLVED`] for `TypeId` / `MethodId`.
    pub const INVALID: StringId = StringId(u32::MAX);

    /// Construct from a raw `u32`. Trivial wrapper for call sites that
    /// already have a `u32` index (e.g. from `Instruction::ConstStr::idx`).
    #[inline]
    pub const fn new(idx: u32) -> Self {
        Self(idx)
    }

    /// Unwrap back to the raw `u32`. Use for `Vec` indexing (`pool[id.0 as usize]`)
    /// or wire-format serialization.
    #[inline]
    pub const fn as_u32(self) -> u32 {
        self.0
    }

    /// True if this is the `INVALID` sentinel.
    #[inline]
    pub fn is_invalid(self) -> bool {
        self.0 == u32::MAX
    }

    /// Look up the underlying `&str` in the given pool. Returns `None` if
    /// `INVALID` or out-of-bounds. Cheaper than `Option<&str>` returning
    /// because no allocation is involved either way.
    #[inline]
    pub fn resolve<'p>(self, pool: &'p [String]) -> Option<&'p str> {
        if self.is_invalid() { return None; }
        pool.get(self.0 as usize).map(String::as_str)
    }

    /// Like [`resolve`] but `panic!`s on out-of-bounds. Use when the caller
    /// has a structural invariant (e.g. resolver populated this ID itself)
    /// and a stale ID indicates a runtime bug.
    #[inline]
    #[track_caller]
    pub fn resolve_unwrap<'p>(self, pool: &'p [String]) -> &'p str {
        self.resolve(pool)
            .unwrap_or_else(|| panic!("StringId({}) out of pool bounds (len={})", self.0, pool.len()))
    }
}

impl From<u32> for StringId {
    fn from(v: u32) -> Self { Self(v) }
}

impl From<StringId> for u32 {
    fn from(v: StringId) -> Self { v.0 }
}

impl fmt::Display for StringId {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.is_invalid() {
            f.write_str("StringId(INVALID)")
        } else {
            write!(f, "StringId({})", self.0)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pool() -> Vec<String> {
        vec![
            "Std.Object".to_string(),
            "Std.Exception".to_string(),
            "".to_string(),  // empty is a valid pool entry
            "hello".to_string(),
        ]
    }

    #[test]
    fn new_and_as_u32_round_trip() {
        let id = StringId::new(42);
        assert_eq!(id.as_u32(), 42);
        assert_eq!(u32::from(id), 42);
        assert_eq!(StringId::from(7).as_u32(), 7);
    }

    #[test]
    fn invalid_sentinel() {
        assert!(StringId::INVALID.is_invalid());
        assert!(!StringId::new(0).is_invalid());
        assert!(!StringId::new(u32::MAX - 1).is_invalid());
    }

    #[test]
    fn resolve_valid_id() {
        let p = pool();
        assert_eq!(StringId::new(0).resolve(&p), Some("Std.Object"));
        assert_eq!(StringId::new(1).resolve(&p), Some("Std.Exception"));
        assert_eq!(StringId::new(2).resolve(&p), Some(""));
        assert_eq!(StringId::new(3).resolve(&p), Some("hello"));
    }

    #[test]
    fn resolve_out_of_bounds_returns_none() {
        let p = pool();
        assert_eq!(StringId::new(99).resolve(&p), None);
        assert_eq!(StringId::new(4).resolve(&p), None);
    }

    #[test]
    fn resolve_invalid_returns_none() {
        let p = pool();
        assert_eq!(StringId::INVALID.resolve(&p), None);
    }

    #[test]
    fn resolve_unwrap_works_for_valid() {
        let p = pool();
        assert_eq!(StringId::new(1).resolve_unwrap(&p), "Std.Exception");
    }

    #[test]
    #[should_panic(expected = "StringId(99) out of pool bounds (len=4)")]
    fn resolve_unwrap_panics_on_out_of_bounds() {
        let p = pool();
        let _ = StringId::new(99).resolve_unwrap(&p);
    }

    #[test]
    fn display_format() {
        assert_eq!(format!("{}", StringId::new(0)), "StringId(0)");
        assert_eq!(format!("{}", StringId::new(42)), "StringId(42)");
        assert_eq!(format!("{}", StringId::INVALID),  "StringId(INVALID)");
    }

    #[test]
    fn equality_and_hashing() {
        use std::collections::HashSet;
        let mut set = HashSet::new();
        set.insert(StringId::new(1));
        set.insert(StringId::new(1));   // dedup
        set.insert(StringId::new(2));
        set.insert(StringId::INVALID);
        assert_eq!(set.len(), 3);
        assert!(set.contains(&StringId::new(1)));
        assert!(set.contains(&StringId::INVALID));
    }

    #[test]
    fn ordering_for_btreemap_use() {
        // StringId is Ord so it can key BTreeMap-based dispatch tables
        let mut ids = vec![StringId::new(3), StringId::new(1), StringId::new(2)];
        ids.sort();
        assert_eq!(ids, vec![StringId::new(1), StringId::new(2), StringId::new(3)]);
    }

    #[test]
    fn size_is_four_bytes() {
        // The whole point of the refactor — confirm we're not paying
        // anything beyond a raw u32.
        assert_eq!(std::mem::size_of::<StringId>(),       4);
        assert_eq!(std::mem::align_of::<StringId>(),      4);
        assert_eq!(std::mem::size_of::<Option<StringId>>(), 8);  // niche-opt not available; document baseline
    }
}
