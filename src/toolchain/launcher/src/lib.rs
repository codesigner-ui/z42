//! Shared host-resolve logic for the z42 launcher trampoline (`z42`).
//!
//! The trampoline locates a "launcher runtime" (`z42vm` + `launcher.zpkg` +
//! `libs`) and execs the z42-written launcher core, forwarding argv after `--`.
//!
//! The **app run path** (locate a bare z42vm and run an app's zpkg directly —
//! used by the apphost stub) lives in the `z42-hostrun` crate
//! (apphost-to-workload, 2026-06-18) and is re-exported here for back-compat.

use std::env;
use std::path::{Path, PathBuf};
use std::process::{exit, Command};

// Shared "locate z42 home / vm" primitives + app run path now live in
// z42-hostrun; re-exported so existing `z42_launcher::{...}` callers (the `z42`
// bin, the apphost stub) keep working until apphost moves to the workload (step 2).
pub use z42_hostrun::{
    env_z42_home, exec_app, home_z42, probe_app_runtime, resolve_app_runtime,
    resolve_app_runtime_in, vm_name, AppRuntime,
};

/// Trampoline's `$Z42_HOME`: env override, else `$HOME/.z42`, else `./.z42`.
pub fn z42_home() -> PathBuf {
    env_z42_home()
        .or_else(home_z42)
        .unwrap_or_else(|| PathBuf::from(".z42"))
}

/// A located launcher runtime.
#[derive(Debug, Clone)]
pub struct Runtime {
    pub vm: PathBuf,
    pub core: PathBuf,
    pub libs: PathBuf,
    pub portable: bool,
}

/// Probe a single directory for a launcher runtime. Requires
/// `<dir>/launcher.zpkg`; `z42vm` may sit directly in `<dir>` (installed
/// layout → `portable=false`) or in `<dir>/bin` (portable package layout,
/// launcher-at-package-root → `portable=true`). `libs` is always `<dir>/libs`.
pub fn probe_runtime(dir: &Path) -> Option<Runtime> {
    let core = dir.join("launcher.zpkg");
    if !core.exists() {
        return None;
    }
    let direct = dir.join(vm_name());
    if direct.exists() {
        return Some(Runtime { vm: direct, core, libs: dir.join("libs"), portable: false });
    }
    let nested = dir.join("bin").join(vm_name());
    if nested.exists() {
        return Some(Runtime { vm: nested, core, libs: dir.join("libs"), portable: true });
    }
    None
}

/// Trampoline resolution: installed (`$Z42_HOME/launcher`) wins, else a
/// package-relative portable layout where the trampoline sits at the package
/// root next to `bin/z42vm` + `launcher.zpkg` + `libs`.
pub fn resolve_trampoline_runtime() -> Option<Runtime> {
    if let Some(rt) = probe_runtime(&z42_home().join("launcher")) {
        return Some(rt);
    }
    let pkg = env::current_exe().ok().and_then(|e| e.parent().map(Path::to_path_buf))?;
    probe_runtime(&pkg)
}

/// Exec `z42vm launcher.zpkg -- <core_args>`, setting `Z42_LIBS` (and the
/// portable hints when applicable), and propagate the child exit code.
/// Never returns.
pub fn exec_core(rt: &Runtime, core_args: &[String]) -> ! {
    let mut cmd = Command::new(&rt.vm);
    cmd.arg(&rt.core);
    if rt.libs.is_dir() {
        cmd.env("Z42_LIBS", &rt.libs);
    }
    if rt.portable {
        cmd.env("Z42_PORTABLE_VM", &rt.vm);
        if rt.libs.is_dir() {
            cmd.env("Z42_PORTABLE_LIBS", &rt.libs);
        }
    }
    if !core_args.is_empty() {
        cmd.arg("--");
        cmd.args(core_args);
    }
    match cmd.status() {
        Ok(status) => exit(status.code().unwrap_or(1)),
        Err(e) => {
            eprintln!("z42: failed to launch runtime ({}): {e}", rt.vm.display());
            exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::sync::atomic::{AtomicUsize, Ordering};

    static COUNTER: AtomicUsize = AtomicUsize::new(0);

    fn temp_dir(tag: &str) -> PathBuf {
        let n = COUNTER.fetch_add(1, Ordering::SeqCst);
        let d = env::temp_dir().join(format!("z42-launcher-test-{}-{}-{}", std::process::id(), tag, n));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    /// Materialize a launcher runtime at `dir` (installed- or portable-style).
    fn make_runtime(dir: &Path, portable: bool) {
        fs::create_dir_all(dir).unwrap();
        fs::write(dir.join("launcher.zpkg"), b"zpkg").unwrap();
        fs::create_dir_all(dir.join("libs")).unwrap();
        if portable {
            fs::create_dir_all(dir.join("bin")).unwrap();
            fs::write(dir.join("bin").join(vm_name()), b"vm").unwrap();
        } else {
            fs::write(dir.join(vm_name()), b"vm").unwrap();
        }
    }

    #[test]
    fn probe_installed_style() {
        let d = temp_dir("probe-inst");
        make_runtime(&d, false);
        let rt = probe_runtime(&d).expect("found");
        assert!(!rt.portable);
        assert_eq!(rt.vm, d.join(vm_name()));
        assert_eq!(rt.libs, d.join("libs"));
    }

    #[test]
    fn probe_portable_style() {
        let d = temp_dir("probe-port");
        make_runtime(&d, true);
        let rt = probe_runtime(&d).expect("found");
        assert!(rt.portable);
        assert_eq!(rt.vm, d.join("bin").join(vm_name()));
    }

    #[test]
    fn probe_missing_is_none() {
        let d = temp_dir("probe-none");
        assert!(probe_runtime(&d).is_none());   // trampoline probe needs launcher.zpkg
    }
}
