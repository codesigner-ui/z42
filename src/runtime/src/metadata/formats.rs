/// Data structures for z42 compiler output formats (Phase 1: JSON).
///
/// Mirrors the C# types in `z42.IR/PackageTypes.cs` exactly.
/// See `docs/design/compilation.md` for the full spec.
use serde::{Deserialize, Serialize};

use crate::bytecode::Module;

// ── Magic bytes (reserved for Phase 2 binary format) ─────────────────────────

/// Magic bytes for `.zbc` binary format (Phase 2): `"ZBC\0"`
pub const ZBC_MAGIC: [u8; 4] = [0x5A, 0x42, 0x43, 0x00];

/// Magic bytes for `.zpkg` binary format (Phase 2): `"ZPK\0"`
pub const ZPKG_MAGIC: [u8; 4] = [0x5A, 0x50, 0x4B, 0x00];

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

/// External dependency declared in a `.zpkg`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZpkgDep {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
}

/// A `.zpkg` package — unified format for both indexed and packed modes.
///
/// `mode = "indexed"`: `files` lists `.zbc` paths; `modules` is empty.
/// `mode = "packed"`:  `modules` inlines all `ZbcFile`s; `files` is empty.
///
/// `kind = "exe"` has an `entry` function; `kind = "lib"` has `entry = null`.
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
