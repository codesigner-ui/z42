//! Locate a deployed `z42vm` (+ libs) and run an app's zpkg **directly** — the
//! apphost run path. Extracted from the z42 launcher lib (apphost-to-workload,
//! 2026-06-18) so the apphost stub (owned by the desktop workload) and the
//! launcher trampoline share one impl, without the workload depending on the
//! launcher crate.
//!
//! "Find a bare z42vm + libs and exec `z42vm app.zpkg`" — no `launcher.zpkg`,
//! no muxer, single VM. Zero external dependencies (pure std).

use std::env;
use std::path::{Path, PathBuf};
use std::process::{exit, Command};

/// Platform `z42vm` filename.
pub fn vm_name() -> &'static str {
    if cfg!(windows) {
        "z42vm.exe"
    } else {
        "z42vm"
    }
}

/// `$Z42_HOME` if set and non-empty.
pub fn env_z42_home() -> Option<PathBuf> {
    match env::var("Z42_HOME") {
        Ok(h) if !h.is_empty() => Some(PathBuf::from(h)),
        _ => None,
    }
}

/// The per-user home `$HOME/.z42` (`%USERPROFILE%\.z42` on Windows).
pub fn home_z42() -> Option<PathBuf> {
    for key in ["HOME", "USERPROFILE"] {
        if let Ok(home) = env::var(key) {
            if !home.is_empty() {
                return Some(PathBuf::from(home).join(".z42"));
            }
        }
    }
    None
}

/// A located z42vm + libs for running a deployed app's zpkg **directly**
/// (apphost run path). There is no `launcher.zpkg`: the apphost bypasses the
/// launcher core and runs `z42vm app.zpkg` itself.
#[derive(Debug, Clone)]
pub struct AppRuntime {
    pub vm: PathBuf,
    pub libs: PathBuf,
}

/// Probe a directory for a runnable z42vm (+ adjacent `libs`). z42vm may sit
/// directly in `<dir>` (installed layout) or in `<dir>/bin` (portable package).
/// Does **not** require `launcher.zpkg` — the apphost runs the app's zpkg directly.
pub fn probe_app_runtime(dir: &Path) -> Option<AppRuntime> {
    let direct = dir.join(vm_name());
    let nested = dir.join("bin").join(vm_name());
    let vm = if direct.exists() {
        direct
    } else if nested.exists() {
        nested
    } else {
        return None;
    };
    Some(AppRuntime { vm, libs: dir.join("libs") })
}

/// apphost run-path resolution (framework-dependent, local-first): locate a
/// z42vm + libs to run the app's zpkg directly. Search order:
///   1. `$Z42_HOME/launcher`        (explicit config override)
///   2. local: walk up from `exe_dir`, `<d>/.z42/launcher` then `<d>/.z42`
///   3. system: `$HOME/.z42/launcher`
pub fn resolve_app_runtime(exe_dir: &Path) -> Option<AppRuntime> {
    resolve_app_runtime_in(env_z42_home().as_deref(), exe_dir, home_z42().as_deref())
}

/// Pure form of [`resolve_app_runtime`] with the home roots injected (tests).
pub fn resolve_app_runtime_in(
    env_home: Option<&Path>,
    exe_dir: &Path,
    sys_home: Option<&Path>,
) -> Option<AppRuntime> {
    // 1. $Z42_HOME override.
    if let Some(h) = env_home {
        if let Some(rt) = probe_app_runtime(&h.join("launcher")) {
            return Some(rt);
        }
    }
    // 2. local: from the exe's dir upward to the filesystem root.
    let mut cur = Some(exe_dir);
    while let Some(d) = cur {
        let dotz42 = d.join(".z42");
        if let Some(rt) = probe_app_runtime(&dotz42.join("launcher")) {
            return Some(rt);
        }
        if let Some(rt) = probe_app_runtime(&dotz42) {
            return Some(rt);
        }
        cur = d.parent();
    }
    // 3. system.
    if let Some(h) = sys_home {
        if let Some(rt) = probe_app_runtime(&h.join("launcher")) {
            return Some(rt);
        }
    }
    None
}

/// Exec `z42vm <app_zpkg> -- <argv>` directly (apphost run path) with
/// `Z42_LIBS` set, and propagate the child exit code. No `launcher.zpkg`, no
/// muxer, single VM. Never returns.
pub fn exec_app(rt: &AppRuntime, app_zpkg: &Path, argv: &[String]) -> ! {
    let mut cmd = Command::new(&rt.vm);
    cmd.arg(app_zpkg);
    if rt.libs.is_dir() {
        cmd.env("Z42_LIBS", &rt.libs);
    }
    if !argv.is_empty() {
        cmd.arg("--");
        cmd.args(argv);
    }
    match cmd.status() {
        Ok(status) => exit(status.code().unwrap_or(1)),
        Err(e) => {
            eprintln!("apphost: failed to launch z42vm ({}): {e}", rt.vm.display());
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

    /// A fresh, unique temp dir (no external `tempfile` dep — zero-dependency).
    fn temp_dir(tag: &str) -> PathBuf {
        let n = COUNTER.fetch_add(1, Ordering::SeqCst);
        let d = env::temp_dir().join(format!("z42-hostrun-test-{}-{}-{}", std::process::id(), tag, n));
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        d
    }

    /// Materialize a runtime at `dir` (installed- or portable-style). Writes a
    /// `launcher.zpkg` too (harmless for the app-runtime probe, which ignores it).
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
    fn probe_app_runtime_needs_no_launcher_zpkg() {
        // apphost run path requires only z42vm (+ libs), NOT launcher.zpkg.
        let d = temp_dir("app-nolaunch");
        fs::write(d.join(vm_name()), b"vm").unwrap();
        fs::create_dir_all(d.join("libs")).unwrap();
        let rt = probe_app_runtime(&d).expect("apphost probe finds z42vm+libs without launcher.zpkg");
        assert_eq!(rt.vm, d.join(vm_name()));
        assert_eq!(rt.libs, d.join("libs"));
    }

    #[test]
    fn probe_app_missing_is_none() {
        let d = temp_dir("app-none");
        assert!(probe_app_runtime(&d).is_none());
    }

    #[test]
    fn z42_home_override_wins() {
        let env_home = temp_dir("env");
        let local_base = temp_dir("local");
        let sys = temp_dir("sys");
        make_runtime(&env_home.join("launcher"), false);
        make_runtime(&local_base.join(".z42").join("launcher"), false);
        make_runtime(&sys.join("launcher"), false);
        let rt = resolve_app_runtime_in(Some(&env_home), &local_base, Some(&sys)).expect("found");
        assert!(rt.vm.starts_with(&env_home));
    }

    #[test]
    fn local_beats_system() {
        let local_base = temp_dir("local2");
        let sys = temp_dir("sys2");
        make_runtime(&local_base.join(".z42").join("launcher"), false);
        make_runtime(&sys.join("launcher"), false);
        let rt = resolve_app_runtime_in(None, &local_base, Some(&sys)).expect("found");
        assert!(rt.vm.starts_with(&local_base));
    }

    #[test]
    fn local_walk_upward_finds_ancestor() {
        let base = temp_dir("walk");
        make_runtime(&base.join(".z42").join("launcher"), false);
        let exe_dir = base.join("dist").join("nested");
        fs::create_dir_all(&exe_dir).unwrap();
        let rt = resolve_app_runtime_in(None, &exe_dir, None).expect("found");
        assert!(rt.vm.starts_with(&base));
    }

    #[test]
    fn local_portable_style_dotz42() {
        // `<repo>/.z42` is itself the package root (launcher-at-package-root).
        let base = temp_dir("walk-port");
        make_runtime(&base.join(".z42"), true);
        let exe_dir = base.join("dist");
        fs::create_dir_all(&exe_dir).unwrap();
        let rt = resolve_app_runtime_in(None, &exe_dir, None).expect("found");
        assert!(rt.vm.starts_with(base.join(".z42")));   // .z42/bin/z42vm
    }

    #[test]
    fn system_fallback() {
        let local_base = temp_dir("local3");
        let sys = temp_dir("sys3");
        make_runtime(&sys.join("launcher"), false);
        let rt = resolve_app_runtime_in(None, &local_base, Some(&sys)).expect("found");
        assert!(rt.vm.starts_with(&sys));
    }

    #[test]
    fn nothing_found_is_none() {
        let local_base = temp_dir("local4");
        assert!(resolve_app_runtime_in(None, &local_base, None).is_none());
    }
}
