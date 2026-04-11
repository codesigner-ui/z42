/// Data structures for z42 compiler output formats (Phase 1: JSON).
///
/// Mirrors the C# types in `z42.IR/PackageTypes.cs` exactly.
/// See `docs/design/compilation.md` for the full spec.
use serde::{Deserialize, Serialize};

use super::bytecode::Module;

// ── Magic bytes ───────────────────────────────────────────────────────────────

/// Magic bytes for `.zbc` binary format: `"ZBC\0"`
pub const ZBC_MAGIC: [u8; 4] = [0x5A, 0x42, 0x43, 0x00];

/// Magic bytes for `.zpkg` binary format: `"ZPK\0"`
pub const ZPKG_MAGIC: [u8; 4] = [0x5A, 0x50, 0x4B, 0x00];

// ── zbc binary format constants ───────────────────────────────────────────────

/// zbc format version written by this toolchain.
pub const ZBC_VERSION: [u16; 2] = [0, 4];

/// `flags` bit 0: metadata (SIGS/EXPT/IMPT) has been stripped; requires zpkg index.
pub const ZBC_FLAG_STRIPPED: u16 = 0x01;
/// `flags` bit 1: file contains a DBUG section.
pub const ZBC_FLAG_HAS_DEBUG: u16 = 0x02;

// ── zbc section tags (4-byte ASCII) ──────────────────────────────────────────

pub const SEC_NSPC: &[u8; 4] = b"NSPC"; // namespace — always first
pub const SEC_STRS: &[u8; 4] = b"STRS"; // string heap (full)
pub const SEC_TYPE: &[u8; 4] = b"TYPE"; // class descriptors (full)
pub const SEC_SIGS: &[u8; 4] = b"SIGS"; // function signatures (full)
pub const SEC_IMPT: &[u8; 4] = b"IMPT"; // import table (full)
pub const SEC_EXPT: &[u8; 4] = b"EXPT"; // export table (full)
pub const SEC_FUNC: &[u8; 4] = b"FUNC"; // function bodies (both)
pub const SEC_BSTR: &[u8; 4] = b"BSTR"; // body string heap (stripped)

// ── .zbc — single-file bytecode unit ─────────────────────────────────────────

/// Compiled output for a single `.z42` source file.
///
/// Analogy: Python `.pyc` = magic + source_hash + code object.
///
/// Phase 1: serialised as JSON.
/// Phase 2: binary sections prefixed by `ZBC_MAGIC`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZbcFile {
    /// Format version `[major, minor]`.
    pub zbc_version: [u16; 2],
    /// Relative path to the original `.z42` source.
    pub source_file: String,
    /// `"sha256:<hex>"` — used for incremental compilation and freshness checks.
    pub source_hash: String,
    /// Top-level namespace of this file, e.g. `"Demo.Greet"`.
    pub namespace: String,
    /// Symbols exported from this file (fully-qualified function / type names).
    pub exports: Vec<String>,
    /// External symbols required by this file (resolved at link / load time).
    pub imports: Vec<String>,
    /// The actual IR — same structure as `.z42ir.json`.
    pub module: Module,
}

impl ZbcFile {
    pub const VERSION: [u16; 2] = [0, 1];
}

// ── .zpkg — unified project package (indexed or packed) ───────────────────────

/// Package kind — distinguishes executable packages from libraries.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ZpkgKind {
    Exe,
    Lib,
}

/// Package storage mode.
///
/// `indexed` — references `.zbc` files on disk (development / incremental mode).
/// `packed`  — inlines all `.zbc` modules (distributable / release mode).
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ZpkgMode {
    Indexed,
    Packed,
}

/// Per-file entry inside a `.zpkg` with `mode = "indexed"`.
/// References a `.zbc` on disk; no inline bytecode.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgFileEntry {
    /// Relative path to the `.z42` source.
    pub source: String,
    /// Relative path to the compiled `.zbc` (under `.cache/` by default).
    pub bytecode: String,
    /// Hash at last successful compile — used for incremental rebuild.
    pub source_hash: String,
    /// Public symbols exported by this file.
    pub exports: Vec<String>,
}

/// Exported symbol entry in a `.zpkg`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgExport {
    /// Fully-qualified name, e.g. `"Demo.Greet.greet"`.
    pub symbol: String,
    /// `"func"`, `"type"`, or `"const"`.
    pub kind: String,
}

/// Resolved dependency entry recorded in a `.zpkg` at compile time.
/// Records the actual file that provided the namespaces, not a declarative constraint.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgDep {
    /// Filename of the dependency (e.g. `"z42-io.zpkg"` or `"utils.zbc"`).
    pub file: String,
    /// Namespaces provided by this dependency that are used by this package.
    #[serde(default)]
    pub namespaces: Vec<String>,
}

/// A `.zpkg` package — unified format for both indexed and packed modes.
///
/// `mode = "indexed"`: `files` lists `.zbc` paths; `modules` is empty.
/// `mode = "packed"`:  `modules` inlines all `ZbcFile`s; `files` is empty.
///
/// `kind = "exe"` has an `entry` function; `kind = "lib"` has `entry = null`.
///
/// `namespaces` lists all namespace names exported by this package.
/// Used by the compiler to resolve `using` declarations without matching on the filename.
///
/// Analogy: C# `.dll` / Java `.jar` (both exe and lib share the same envelope).
///
/// Phase 1: JSON.  Phase 2: binary `ZPKG_MAGIC` + sections.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgFile {
    pub name: String,
    pub version: String,
    /// `"exe"` or `"lib"`.
    pub kind: ZpkgKind,
    /// `"indexed"` or `"packed"`.
    pub mode: ZpkgMode,
    /// All namespace names exported by this package (e.g. `["z42.io", "z42.io.streams"]`).
    /// Used by the compiler to map `using` declarations to package files.
    #[serde(default)]
    pub namespaces: Vec<String>,
    /// All public symbols across every bundled module.
    pub exports: Vec<ZpkgExport>,
    #[serde(default)]
    pub dependencies: Vec<ZpkgDep>,
    /// Non-empty when `mode = "indexed"`.
    #[serde(default)]
    pub files: Vec<ZpkgFileEntry>,
    /// Non-empty when `mode = "packed"`.
    #[serde(default)]
    pub modules: Vec<ZbcFile>,
    /// Qualified entry-point function for `kind = exe`, e.g. `"Hello.main"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub entry: Option<String>,
}

// ── zbc binary helpers ────────────────────────────────────────────────────────

/// Reads only the NSPC section from a zbc v0.2 binary buffer.
///
/// Performs minimal work: reads the 16-byte header then the first section
/// (which must be NSPC by spec).  Returns `Ok("")` for files with no namespace
/// (e.g. top-level scripts).
///
/// # Errors
/// Returns an error if the buffer is too short or has a bad magic.
pub fn read_zbc_namespace(data: &[u8]) -> anyhow::Result<String> {
    use anyhow::bail;

    if data.len() < 16 {
        bail!("zbc buffer too short ({} bytes)", data.len());
    }
    if &data[0..4] != ZBC_MAGIC {
        bail!("not a zbc file (bad magic)");
    }

    // flags at bytes [8..10]
    let _flags = u16::from_le_bytes([data[8], data[9]]);

    // First section starts at byte 16: tag[4] + length[4] + data[length]
    let pos = 16usize;
    if data.len() < pos + 8 {
        bail!("zbc file truncated before first section");
    }
    let tag = &data[pos..pos + 4];
    let len = u32::from_le_bytes([data[pos+4], data[pos+5], data[pos+6], data[pos+7]]) as usize;

    if tag != SEC_NSPC {
        bail!("zbc first section is not NSPC (got {:?})", std::str::from_utf8(tag));
    }
    if data.len() < pos + 8 + len {
        bail!("zbc NSPC section truncated");
    }

    let sec = &data[pos + 8 .. pos + 8 + len];
    if sec.len() < 2 {
        return Ok(String::new());
    }
    let ns_len = u16::from_le_bytes([sec[0], sec[1]]) as usize;
    if ns_len == 0 {
        return Ok(String::new());
    }
    if sec.len() < 2 + ns_len {
        bail!("NSPC section: declared length {} exceeds data", ns_len);
    }
    Ok(std::str::from_utf8(&sec[2..2 + ns_len])?.to_owned())
}

/// Returns true if the zbc binary has the STRIPPED flag set.
/// Does not validate the full file — only reads the header flags field.
pub fn zbc_is_stripped(data: &[u8]) -> bool {
    if data.len() < 10 {
        return false;
    }
    let flags = u16::from_le_bytes([data[8], data[9]]);
    flags & ZBC_FLAG_STRIPPED != 0
}
