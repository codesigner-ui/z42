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

fn main() {
    let launcher_dir = z42_home().join("launcher");
    let vm_name = if cfg!(windows) { "z42vm.exe" } else { "z42vm" };
    let vm = launcher_dir.join(vm_name);
    let core = launcher_dir.join("launcher.zpkg");
    let libs = launcher_dir.join("libs");

    if !vm.exists() || !core.exists() {
        eprintln!(
            "z42: launcher runtime not found under {}.\n\
             Expected {} and launcher.zpkg. Reinstall the z42 launcher.",
            launcher_dir.display(),
            vm_name,
        );
        exit(1);
    }

    // Everything after argv[0] is the user's command line; forward it to the
    // launcher core after `--` so its GetCommandLineArgs() sees exactly it.
    let forwarded: Vec<String> = env::args().skip(1).collect();

    let mut cmd = Command::new(&vm);
    cmd.arg(&core);
    if libs.is_dir() {
        cmd.env("Z42_LIBS", &libs);
    }
    if !forwarded.is_empty() {
        cmd.arg("--");
        cmd.args(&forwarded);
    }

    match cmd.status() {
        Ok(status) => exit(status.code().unwrap_or(1)),
        Err(e) => {
            eprintln!("z42: failed to launch runtime ({}): {e}", vm.display());
            exit(1);
        }
    }
}
