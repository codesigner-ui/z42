//! z42 launcher trampoline (add-z42-launcher, 2026-06-02; shared-lib extracted
//! for add-apphost, 2026-06-09).
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
//! and never bumped per release — all behaviour lives in z42. The resolve/exec
//! mechanics it shares with the per-app `apphost` stub live in `lib.rs`.

use std::env;
use std::process::exit;

use z42_launcher::{exec_core, resolve_trampoline_runtime, vm_name, z42_home};

fn main() {
    let rt = match resolve_trampoline_runtime() {
        Some(rt) => rt,
        None => {
            let vm = vm_name();
            eprintln!(
                "z42: launcher runtime not found.\n\
                 Looked for {}/launcher/{vm}+launcher.zpkg (installed) and a\n\
                 package-relative <pkg>/bin/{vm}+<pkg>/launcher.zpkg (portable, trampoline at pkg root).\n\
                 Reinstall the z42 launcher.",
                z42_home().join("launcher").display(),
            );
            exit(1);
        }
    };

    // Everything after argv[0] is the user's command line; forward it to the
    // launcher core after `--` so its GetCommandLineArgs() sees exactly it.
    let forwarded: Vec<String> = env::args().skip(1).collect();
    exec_core(&rt, &forwarded);
}
