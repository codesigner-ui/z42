//! Native library loader: `dlopen` + invoke the library's `<basename>_register`
//! entry point so its types land in the VM's `TypeRegistry`.
//!
//! The library handle is stored on `VmContext.native_libs` for the VM's
//! lifetime, keeping every `fn_ptr` referenced by a `RegisteredType` valid
//! until VM drop.

use std::ffi::OsStr;
use std::path::Path;

use anyhow::{anyhow, Context, Result};

use super::error::{self, Z0910};
use crate::vm_context::VmContext;

/// Type signature of the library's register entry point — `void(void)`.
type RegisterFn = unsafe extern "C" fn();

/// Load `path`, look up `<libname>_register`, and call it. Errors are
/// returned as `anyhow::Error` and mirrored into the thread-local
/// last-error slot so C consumers see the same diagnostic.
pub fn load_library(ctx: &VmContext, path: &Path) -> Result<()> {
    error::clear();

    let lib = unsafe { libloading::Library::new(path) }
        .with_context(|| format!("Z0910: dlopen({})", path.display()))
        .map_err(|e| {
            error::set(Z0910, format!("{e:#}"));
            e
        })?;

    let entry_name = guess_register_symbol(path)
        .ok_or_else(|| {
            let msg = format!(
                "Z0910: cannot derive register-entry symbol from path {} (file name must end in `<libname>.<ext>` so we can call `<libname>_register`)",
                path.display()
            );
            error::set(Z0910, &msg);
            anyhow!(msg)
        })?;

    let register: libloading::Symbol<RegisterFn> = unsafe { lib.get(entry_name.as_bytes()) }
        .with_context(|| format!("Z0910: missing register symbol `{entry_name}` in {}", path.display()))
        .map_err(|e| {
            error::set(Z0910, format!("{e:#}"));
            e
        })?;

    // Call the entry. The library is expected to invoke `z42_register_type`
    // for each of its types; those calls flow through `exports::z42_register_type`
    // → `VmContext::register_native_type` on the current thread.
    unsafe { register() };

    // Drop the symbol borrow before moving the library into native_libs.
    drop(register);
    ctx.native_libs.borrow_mut().push(lib);
    Ok(())
}

/// Strip lib prefix / extension from `path` and form `<basename>_register`.
///
/// Examples:
/// - `libnumz42_c.dylib`  → `numz42_c_register`
/// - `libnumz42_c.so`     → `numz42_c_register`
/// - `numz42_c.dll`       → `numz42_c_register`
pub fn guess_register_symbol(path: &Path) -> Option<String> {
    let stem = path.file_stem().and_then(OsStr::to_str)?;
    let core = stem.strip_prefix("lib").unwrap_or(stem);
    Some(format!("{core}_register"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn guess_register_symbol_strips_prefix_and_ext() {
        for (input, expected) in [
            ("libnumz42_c.dylib", "numz42_c_register"),
            ("libnumz42_c.so",    "numz42_c_register"),
            ("numz42_c.dll",      "numz42_c_register"),
            ("/abs/path/libfoo.dylib", "foo_register"),
        ] {
            assert_eq!(
                guess_register_symbol(&PathBuf::from(input)).as_deref(),
                Some(expected),
                "input = {input}"
            );
        }
    }

    #[test]
    fn nonexistent_path_sets_z0910() {
        let ctx = VmContext::new();
        let err = load_library(&ctx, Path::new("/nonexistent/lib_definitely_not_here.dylib"))
            .expect_err("must fail");
        assert!(format!("{err:#}").contains("Z0910"));
        assert_eq!(error::last().code, Z0910);
    }
}
