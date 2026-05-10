//! Hello-world demo for the Tier 2 `z42-host` API.
//!
//! Spec: docs/design/runtime/embedding.md §9.3.
//!
//! Usage (from repo root):
//!
//! ```sh
//! # 1. Make sure z42c.dll and the stdlib zpkgs are built.
//! dotnet build src/compiler/z42.slnx
//!
//! # 2. Compile the sample source to .zbc.
//! dotnet artifacts/compiler/z42.Driver/bin/z42c.dll \
//!     src/runtime/tests/data/embedding_hello/source.z42 \
//!     --emit zbc -o /tmp/embedding_hello.zbc
//!
//! # 3. Run the host demo, pointing at the libs dir for corelib.
//! cargo run --manifest-path src/toolchain/host/examples/hello_rust/Cargo.toml -- \
//!     /tmp/embedding_hello.zbc artifacts/z42/libs
//! ```
//!
//! Expected stdout:
//!
//! ```text
//! [host] Hello, World!
//! ```
//!
//! The bracketed `[host]` prefix proves the line went through the host
//! sink rather than the runtime's default stdout — i.e. the embedding
//! API is in control of the output.

use std::env;
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::{Arc, Mutex};

use z42_host::{ExecMode, Host, HostConfig};

fn main() -> ExitCode {
    let args: Vec<String> = env::args().collect();
    if args.len() != 3 {
        eprintln!("usage: {} <hello.zbc> <libs_dir>", args[0]);
        return ExitCode::from(2);
    }
    let zbc_path = PathBuf::from(&args[1]);
    let libs_dir = PathBuf::from(&args[2]);

    let captured: Arc<Mutex<Vec<u8>>> = Arc::new(Mutex::new(Vec::new()));
    let captured_clone = Arc::clone(&captured);

    let cfg = HostConfig {
        exec_mode: ExecMode::Interp,
        stdout: Some(Box::new(move |bytes: &[u8]| {
            // Tag with [host] so it's obvious the host sink fired.
            print!("[host] ");
            std::io::Write::write_all(&mut std::io::stdout(), bytes).ok();
            captured_clone.lock().unwrap().extend_from_slice(bytes);
        })),
        search_paths: vec![libs_dir],
        ..Default::default()
    };

    let host = match Host::new(cfg) {
        Ok(h) => h,
        Err(e) => {
            eprintln!("z42_host_initialize failed: {e}");
            return ExitCode::from(1);
        }
    };

    let module = match host.load_zbc_path(&zbc_path) {
        Ok(m) => m,
        Err(e) => {
            eprintln!("load_zbc({}) failed: {e}", zbc_path.display());
            return ExitCode::from(1);
        }
    };

    let entry = match host.resolve_entry(&module, "Embedding.Hello.Main") {
        Ok(e) => e,
        Err(err) => {
            eprintln!("resolve_entry failed: {err}");
            return ExitCode::from(1);
        }
    };

    if let Err(e) = host.invoke(&entry, &[]) {
        eprintln!("invoke failed: {e}");
        return ExitCode::from(1);
    }

    let stdout_text = String::from_utf8_lossy(&captured.lock().unwrap()).into_owned();
    if stdout_text != "Hello, World!\n" {
        eprintln!(
            "ERROR: host sink expected `Hello, World!\\n`, got {stdout_text:?}"
        );
        return ExitCode::from(1);
    }
    ExitCode::SUCCESS
}
