//! z42 launcher trampoline (add-z42-launcher, 2026-06-02).
//!
//! The single binary users install (`z42`). It is **version-agnostic** and
//! does the bare minimum that genuinely cannot be written in z42 (you can't
//! run z42 without a VM): locate the bundled "launcher runtime" and hand off
//! to the z42-written launcher core, forwarding all argv.
//!
//! ```text
//! z42 <argv...>
//!   → exec  $Z42_HOME/launcher/z42vm  $Z42_HOME/launcher/launcher.zpkg  --  <argv...>
//!           (with Z42_LIBS = $Z42_HOME/launcher/libs)
//! ```
//!
//! The launcher core (core/launcher.z42 → launcher.zpkg, Exe-mode) parses the
//! argv via GetCommandLineArgs, resolves the app runtime version, and runs the
//! user's app with the *resolved* z42vm. This trampoline is intentionally tiny
//! and never bumped per release — all behaviour lives in z42.

use std::env;
use std::path::PathBuf;
use std::process::{exit, Command};

/// `$Z42_HOME`, else `$HOME/.z42`, else `./.z42`.
fn z42_home() -> PathBuf {
    if let Ok(h) = env::var("Z42_HOME") {
        if !h.is_empty() {
            return PathBuf::from(h);
        }
    }
    // Windows uses USERPROFILE; fall back to HOME then cwd-relative.
    for key in ["HOME", "USERPROFILE"] {
        if let Ok(home) = env::var(key) {
            if !home.is_empty() {
                return PathBuf::from(home).join(".z42");
            }
        }
    }
    PathBuf::from(".z42")
}

/// Where the launcher runtime (z42vm + launcher.zpkg + libs) lives, and
/// whether we're in a portable (package-relative) install.
struct Runtime {
    vm: PathBuf,
    core: PathBuf,
    libs: PathBuf,
    portable: bool,
}

/// Resolve the launcher runtime. Installed mode ($Z42_HOME/launcher) wins;
/// otherwise fall back to a portable, package-relative layout where this
/// trampoline sits at the package root `<pkg>/z42` next to `<pkg>/bin/z42vm`,
/// `<pkg>/launcher.zpkg`, and `<pkg>/libs/` (launcher-at-package-root).
fn resolve_runtime(vm_name: &str) -> Option<Runtime> {
    // 1. Installed: $Z42_HOME/launcher/{z42vm, launcher.zpkg, libs}
    let installed = z42_home().join("launcher");
    if installed.join(vm_name).exists() && installed.join("launcher.zpkg").exists() {
        return Some(Runtime {
            vm: installed.join(vm_name),
            core: installed.join("launcher.zpkg"),
            libs: installed.join("libs"),
            portable: false,
        });
    }
    // 2. Portable: <pkg>/z42 → <pkg>/{bin/z42vm, launcher.zpkg, libs}
    //    The trampoline lives at the package ROOT, so exe.parent() IS the
    //    package root (launcher-at-package-root). z42vm stays in bin/.
    let pkg = std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|p| p.to_path_buf())); // pkg root (z42 at root)
    if let Some(pkg) = pkg {
        let vm = pkg.join("bin").join(vm_name);
        let core = pkg.join("launcher.zpkg");
        if vm.exists() && core.exists() {
            return Some(Runtime { vm, core, libs: pkg.join("libs"), portable: true });
        }
    }
    None
}

fn main() {
    let vm_name = if cfg!(windows) { "z42vm.exe" } else { "z42vm" };
    let rt = match resolve_runtime(vm_name) {
        Some(rt) => rt,
        None => {
            eprintln!(
                "z42: launcher runtime not found.\n\
                 Looked for {}/launcher/{vm_name}+launcher.zpkg (installed) and a\n\
                 package-relative <pkg>/bin/{vm_name}+<pkg>/launcher.zpkg (portable, trampoline at pkg root).\n\
                 Reinstall the z42 launcher.",
                z42_home().join("launcher").display(),
            );
            exit(1);
        }
    };

    // Everything after argv[0] is the user's command line; forward it to the
    // launcher core after `--` so its GetCommandLineArgs() sees exactly it.
    let forwarded: Vec<String> = env::args().skip(1).collect();

    let mut cmd = Command::new(&rt.vm);
    cmd.arg(&rt.core);
    if rt.libs.is_dir() {
        cmd.env("Z42_LIBS", &rt.libs);
    }
    if rt.portable {
        // No configured ~/.z42 runtimes; tell the launcher core to run apps
        // with this bundled runtime directly.
        cmd.env("Z42_PORTABLE_VM", &rt.vm);
        if rt.libs.is_dir() {
            cmd.env("Z42_PORTABLE_LIBS", &rt.libs);
        }
    }
    if !forwarded.is_empty() {
        cmd.arg("--");
        cmd.args(&forwarded);
    }

    match cmd.status() {
        Ok(status) => exit(status.code().unwrap_or(1)),
        Err(e) => {
            eprintln!("z42: failed to launch runtime ({}): {e}", rt.vm.display());
            exit(1);
        }
    }
}
