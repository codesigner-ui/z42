//! Z42Value ↔ z42 Value conversion (blittable subset only).
//!
//! Spec C2 only marshals primitives + raw pointers. String / Array borrow
//! via `pinned` blocks lands in C4; managed object handles go through
//! `Z42_VALUE_TAG_OBJECT` once script-defined classes can cross FFI in C5.

use std::os::raw::c_void;

use anyhow::{anyhow, Result};
use z42_abi::Z42Value;

use crate::metadata::Value;
use crate::native::dispatch::{
    self, SigType, Z42_VALUE_TAG_BOOL, Z42_VALUE_TAG_F64, Z42_VALUE_TAG_I64,
    Z42_VALUE_TAG_NATIVEPTR, Z42_VALUE_TAG_NULL,
};

/// Convert a z42 [`Value`] into a [`Z42Value`] suitable for passing to a
/// native method whose argument type is `target`.
pub fn value_to_z42(v: &Value, target: &SigType) -> Result<Z42Value> {
    match (v, target) {
        // Integer-family targets accept Value::I64 (any narrowing happens
        // at libffi cif level — only the low N bytes are read).
        (Value::I64(n), SigType::I8 | SigType::I16 | SigType::I32 | SigType::I64
                       | SigType::U8 | SigType::U16 | SigType::U32 | SigType::U64) => {
            Ok(dispatch::z42_i64(*n))
        }
        // bool ↔ bool
        (Value::Bool(b), SigType::Bool) => Ok(dispatch::z42_bool(*b)),
        // f64 / f32 ← Value::F64
        (Value::F64(x), SigType::F64) => Ok(dispatch::z42_f64(*x)),
        (Value::F64(x), SigType::F32) => Ok(dispatch::z42_f64(*x)),
        // Native pointer slots accept Value::I64 (caller stores ptr-as-i64
        // until C5 introduces a real Value::NativePtr wrapper).
        (Value::I64(n), SigType::Ptr | SigType::SelfRef) => {
            Ok(dispatch::z42_native_ptr(*n as usize as *mut c_void))
        }
        (Value::Null, SigType::Ptr | SigType::SelfRef | SigType::CStr) => {
            Ok(dispatch::z42_native_ptr(std::ptr::null_mut()))
        }
        // Spec C4 — PinnedView projects to either its raw pointer or its
        // length, depending on the target ABI type. C5's source generator
        // emits separate `FieldGet view, "ptr"` / `FieldGet view, "len"`
        // before the call site, but a defensive fall-through here lets a
        // hand-crafted IR pass the view directly when convenient.
        (Value::PinnedView { ptr, .. }, SigType::Ptr | SigType::SelfRef | SigType::CStr) => {
            Ok(dispatch::z42_native_ptr(*ptr as usize as *mut c_void))
        }
        (
            Value::PinnedView { len, .. },
            SigType::U64 | SigType::I64 | SigType::U32 | SigType::I32,
        ) => Ok(dispatch::z42_i64(*len as i64)),
        (v, ty) => Err(anyhow!(
            "marshal: cannot pass z42 {v:?} as native arg of type {ty:?} (C2 blittable subset only; pinned/object marshalling lands in C4/C5)"
        )),
    }
}

/// Convert the [`Z42Value`] returned from a native method into a z42 [`Value`].
pub fn z42_to_value(z: &Z42Value, source: &SigType) -> Result<Value> {
    match (z.tag, source) {
        (Z42_VALUE_TAG_NULL, SigType::Void) => Ok(Value::Null),
        (Z42_VALUE_TAG_NULL, _) => Ok(Value::Null),
        (Z42_VALUE_TAG_I64, _) => Ok(Value::I64(z.payload as i64)),
        (Z42_VALUE_TAG_F64, _) => Ok(Value::F64(f64::from_bits(z.payload))),
        (Z42_VALUE_TAG_BOOL, _) => Ok(Value::Bool(z.payload != 0)),
        (Z42_VALUE_TAG_NATIVEPTR, _) => {
            // C2 stores native pointers as Value::I64; C5 will introduce
            // a typed wrapper.
            Ok(Value::I64(z.payload as i64))
        }
        (tag, _) => Err(anyhow!(
            "z42_to_value: unsupported tag {tag} in C2 (Str/Object marshalling lands in C4/C5)"
        )),
    }
}
