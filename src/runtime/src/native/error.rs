//! Thread-local last-error slot for `z42_last_error()`.
//!
//! Each `z42_*` ABI entry point clears the slot on entry, then either
//! succeeds (slot stays cleared) or stores a `Z42Error` describing the
//! failure. The slot is per-thread so concurrent z42 hosts don't clobber
//! each other's diagnostics.

use std::cell::Cell;
use std::ffi::CString;
use std::os::raw::c_char;

use z42_abi::Z42Error;

// Z42 error codes registered to spec C2 (`impl-tier1-c-abi`).
pub const Z0905: u32 = 905; // NativeTypeRegistrationFailure
pub const Z0906: u32 = 906; // AbiVersionMismatch
pub const Z0910: u32 = 910; // NativeLibraryLoadFailure

thread_local! {
    /// Last-error slot. Cleared by each `z42_*` entry on call; set on
    /// failure. `Z42Error.message` points into a leaked `CString` — short
    /// error count keeps the leak insignificant and avoids ABI concerns
    /// about who owns the buffer (caller is told "do not free").
    static LAST_ERROR: Cell<Z42Error> = const { Cell::new(NO_ERROR) };
}

pub const NO_ERROR: Z42Error = Z42Error {
    code: 0,
    message: std::ptr::null(),
};

/// Reset the slot. Called by every `z42_*` entry point.
pub(crate) fn clear() {
    LAST_ERROR.with(|c| c.set(NO_ERROR));
}

/// Record an error on the calling thread.
///
/// The message is converted to a NUL-terminated C string and intentionally
/// leaked so the returned pointer stays valid until the thread exits.
/// Diagnostic paths only — never call this in a tight loop.
pub(crate) fn set(code: u32, message: impl Into<String>) {
    let s = message.into();
    let cstring = CString::new(s)
        .unwrap_or_else(|_| CString::new("(error message contained interior NUL)").unwrap());
    // `into_raw()` transfers ownership to the leaked allocation; we never
    // reclaim it. ~50 bytes per failure × low call rate is acceptable.
    let raw: *const c_char = cstring.into_raw();
    LAST_ERROR.with(|c| c.set(Z42Error { code, message: raw }));
}

/// Read the current thread's last error. Multiple reads return the same
/// value until the next `z42_*` entry point resets the slot.
pub fn last() -> Z42Error {
    LAST_ERROR.with(|c| c.get())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clear_and_set_round_trip() {
        clear();
        assert_eq!(last().code, 0);
        set(Z0906, "ABI version mismatch: expected 1, got 2");
        let e = last();
        assert_eq!(e.code, Z0906);
        let msg = unsafe { std::ffi::CStr::from_ptr(e.message) }.to_string_lossy();
        assert!(msg.contains("ABI version mismatch"));
    }

    #[test]
    fn clear_resets_to_no_error() {
        set(Z0905, "stale");
        clear();
        assert_eq!(last().code, 0);
        assert!(last().message.is_null());
    }
}
