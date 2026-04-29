//! ABI layout invariants — these tests freeze the C ABI v1 byte layout.
//!
//! Any failure here means we accidentally broke the ABI; either the change is
//! wrong, or it requires bumping `Z42_ABI_VERSION` and updating consumers.

use core::mem::{align_of, offset_of, size_of};
use z42_abi::*;

// ── Z42TypeDescriptor_v1 ────────────────────────────────────────────────────

#[test]
fn abi_version_is_first_field() {
    assert_eq!(offset_of!(Z42TypeDescriptor_v1, abi_version), 0,
        "abi_version MUST be at offset 0 across all ABI versions");
}

#[test]
fn type_descriptor_head_layout() {
    // Head fields are fixed at well-known offsets so older VMs can still read
    // a newer descriptor's basic identity even when trailing fields differ.
    assert_eq!(offset_of!(Z42TypeDescriptor_v1, abi_version), 0);
    assert_eq!(offset_of!(Z42TypeDescriptor_v1, flags), 4);
    // After two u32 fields we expect natural alignment for a pointer.
    assert_eq!(offset_of!(Z42TypeDescriptor_v1, module_name), 8);
}

#[test]
fn type_descriptor_size_matches_field_sum_64bit() {
    // On 64-bit platforms the descriptor is two u32 + 4 size_t/pointers in the
    // header + 6 fn-pointer slots + 3 (count, ptr) pairs = predictable size.
    // We assert here only on 64-bit targets; 32-bit support deferred.
    if size_of::<usize>() == 8 {
        // 4 + 4 + 8 (modname) + 8 (typename) + 8 + 8        = 40 (head)
        // + 6 * 8 (alloc..release fn ptrs)                  = 88
        // + 8 + 8 (method_count, methods)                   = 104
        // + 8 + 8 (field_count, fields)                     = 120
        // + 8 + 8 (trait_impl_count, trait_impls)           = 136
        assert_eq!(size_of::<Z42TypeDescriptor_v1>(), 136);
    }
}

#[test]
fn type_descriptor_align_is_pointer_sized() {
    assert_eq!(align_of::<Z42TypeDescriptor_v1>(), align_of::<usize>());
}

// ── Z42Value ────────────────────────────────────────────────────────────────

#[test]
fn value_is_16_bytes() {
    assert_eq!(size_of::<Z42Value>(), 16,
        "Z42Value layout (tag:u32, reserved:u32, payload:u64) is frozen at 16 bytes");
    assert_eq!(offset_of!(Z42Value, tag), 0);
    assert_eq!(offset_of!(Z42Value, reserved), 4);
    assert_eq!(offset_of!(Z42Value, payload), 8);
}

// ── Z42Args ─────────────────────────────────────────────────────────────────

#[test]
fn args_layout() {
    assert_eq!(offset_of!(Z42Args, count), 0);
    if size_of::<usize>() == 8 {
        assert_eq!(offset_of!(Z42Args, items), 8);
        assert_eq!(size_of::<Z42Args>(), 16);
    }
}

// ── Z42MethodDesc / Z42FieldDesc ────────────────────────────────────────────

#[test]
fn method_desc_layout() {
    assert_eq!(offset_of!(Z42MethodDesc, name), 0);
    if size_of::<usize>() == 8 {
        assert_eq!(offset_of!(Z42MethodDesc, signature), 8);
        assert_eq!(offset_of!(Z42MethodDesc, fn_ptr), 16);
        assert_eq!(offset_of!(Z42MethodDesc, flags), 24);
        assert_eq!(offset_of!(Z42MethodDesc, reserved), 28);
        assert_eq!(size_of::<Z42MethodDesc>(), 32);
    }
}

#[test]
fn field_desc_layout() {
    assert_eq!(offset_of!(Z42FieldDesc, name), 0);
    if size_of::<usize>() == 8 {
        assert_eq!(offset_of!(Z42FieldDesc, type_name), 8);
        assert_eq!(offset_of!(Z42FieldDesc, offset), 16);
        assert_eq!(offset_of!(Z42FieldDesc, flags), 24);
        assert_eq!(offset_of!(Z42FieldDesc, reserved), 28);
        assert_eq!(size_of::<Z42FieldDesc>(), 32);
    }
}

// ── Constants pinned ────────────────────────────────────────────────────────

#[test]
fn abi_version_constant() {
    assert_eq!(Z42_ABI_VERSION, 1);
}

#[test]
fn descriptor_types_are_sync() {
    // C3 ergonomic macros emit `static` Z42TypeDescriptor_v1 / *MethodDesc /
    // *TraitImpl literals. The crate-level `unsafe impl Sync` makes that
    // work; this test pins the promise.
    fn assert_sync<T: Sync>() {}
    assert_sync::<Z42TypeDescriptor_v1>();
    assert_sync::<Z42MethodDesc>();
    assert_sync::<Z42FieldDesc>();
    assert_sync::<Z42MethodImpl>();
    assert_sync::<Z42TraitImpl>();
}

#[test]
fn flag_bits_pinned() {
    // Frozen wire values; do not change without bumping ABI version.
    assert_eq!(Z42_TYPE_FLAG_VALUE_TYPE, 1);
    assert_eq!(Z42_TYPE_FLAG_SEALED, 2);
    assert_eq!(Z42_TYPE_FLAG_ABSTRACT, 4);
    assert_eq!(Z42_TYPE_FLAG_TRACEABLE, 8);

    assert_eq!(Z42_METHOD_FLAG_STATIC, 1);
    assert_eq!(Z42_METHOD_FLAG_VIRTUAL, 2);
    assert_eq!(Z42_METHOD_FLAG_OVERRIDE, 4);
    assert_eq!(Z42_METHOD_FLAG_CTOR, 8);

    assert_eq!(Z42_FIELD_FLAG_READONLY, 1);
    assert_eq!(Z42_FIELD_FLAG_INTERNAL, 2);
}

#[test]
fn value_tag_constants_pinned() {
    // Frozen wire values — entries appended only across ABI versions.
    assert_eq!(Z42_VALUE_TAG_NULL,        0);
    assert_eq!(Z42_VALUE_TAG_I64,         1);
    assert_eq!(Z42_VALUE_TAG_F64,         2);
    assert_eq!(Z42_VALUE_TAG_BOOL,        3);
    assert_eq!(Z42_VALUE_TAG_STR,         4);
    assert_eq!(Z42_VALUE_TAG_OBJECT,      5);
    assert_eq!(Z42_VALUE_TAG_TYPEREF,     6);
    assert_eq!(Z42_VALUE_TAG_NATIVEPTR,   7);
    assert_eq!(Z42_VALUE_TAG_PINNED_VIEW, 8);
}
