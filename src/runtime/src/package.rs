/// z42 compilation artifact formats — Phase 1 (JSON) data structures.
///
/// Two granularity levels:
///   - File-level:    ZbcFile   (.zbc)  + ZmodManifest (.zmod)
///   - Assembly-level: ZlibFile (.zlib)
///
/// See docs/design/compilation.md for the full design.
use serde::{Deserialize, Serialize};

use crate::bytecode::Module;

// ── Magic bytes ────────────────────────────────────────────────────────────────

/// Magic bytes for `.zbc` binary format (Phase 2): `"ZBC\0"`
pub const ZBC_MAGIC: [u8; 4] = [0x5A, 0x42, 0x43, 0x00];

/// Magic bytes for `.zlib` binary format (Phase 2): `"ZLB\0"`
pub const ZLIB_MAGIC: [u8; 4] = [0x5A, 0x4C, 0x42, 0x00];

// ── .zbc — single-file bytecode ───────────────────────────────────────────────

/// A compiled single-source-file unit.
/// Phase 1: serialised as JSON (`.zbc` with `--debug`) or embedded in `.zmod` / `.zlib`.
/// Phase 2: packed into binary sections prefixed by ZBC_MAGIC.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZbcFile {
    /// Format version `[major, minor]`.
    pub zbc_version: [u16; 2],
    /// Relative path to the original `.z42` source.
    pub source_file: String,
    /// `"sha256:<hex>"` — used for incremental compilation.
    pub source_hash: String,
    /// Top-level namespace of this file, e.g. `"Demo.Greet"`.
    pub namespace: String,
    /// Symbols exported from this file (function/type names).
    pub exports: Vec<String>,
    /// External symbols required by this file (qualified names).
    pub imports: Vec<String>,
    /// The actual IR — same structure as `.z42ir.json`.
    pub module: Module,
}

impl ZbcFile {
    pub const VERSION: [u16; 2] = [0, 1];
}

// ── .zmod — module manifest / project index ───────────────────────────────────

/// Per-file entry inside a `.zmod` manifest.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZmodFileEntry {
    /// Relative path to the `.z42` source.
    pub source: String,
    /// Relative path to the compiled `.zbc` (under `.cache/` by default).
    pub bytecode: String,
    /// Hash at last successful compile — used for incremental rebuild.
    pub source_hash: String,
    /// Public symbols exported by this file.
    pub exports: Vec<String>,
}

/// External dependency declared in a `.zmod`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZmodDep {
    pub name: String,
    /// Local path or registry reference to a `.zlib`.
    pub path: String,
    /// SemVer constraint, e.g. `">=0.1"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
}

/// The `.zmod` manifest — project-level index of `.zbc` files and dependencies.
/// Always JSON; VCS-friendly.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZmodManifest {
    pub zmod_version: [u16; 2],
    /// Project/library name.
    pub name: String,
    pub version: String,
    /// `"lib"` (no entry point) or `"exe"` (has `entry`).
    pub kind: ZmodKind,
    /// All source files in this project.
    pub files: Vec<ZmodFileEntry>,
    /// External `.zlib` dependencies.
    #[serde(default)]
    pub dependencies: Vec<ZmodDep>,
    /// Qualified entry-point function for `kind = "exe"`, e.g. `"Hello.Main"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub entry: Option<String>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ZmodKind {
    Lib,
    Exe,
}

impl ZmodManifest {
    pub const VERSION: [u16; 2] = [0, 1];

    /// Returns the names of files whose `source_hash` differs from `current_hash`.
    /// Caller is responsible for computing hashes.
    pub fn stale_files<'a>(&'a self, current_hashes: &[(&str, &str)]) -> Vec<&'a ZmodFileEntry> {
        self.files
            .iter()
            .filter(|f| {
                current_hashes
                    .iter()
                    .find(|(src, _)| *src == f.source)
                    .map(|(_, hash)| *hash != f.source_hash)
                    .unwrap_or(true) // missing → stale
            })
            .collect()
    }
}

// ── .zlib — assembly / library bundle ────────────────────────────────────────

/// Exported symbol entry inside a `.zlib`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZlibExport {
    /// Fully-qualified name, e.g. `"Demo.Greet.Greet"`.
    pub symbol: String,
    /// `"func"`, `"type"`, `"const"`.
    pub kind: String,
}

/// External dependency declared in a `.zlib`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZlibDep {
    pub name: String,
    pub version: String,
}

/// A `.zlib` assembly — bundles all `.zbc` files of a project into one distributable.
/// Phase 1: JSON (all `ZbcFile`s inlined in `modules`).
/// Phase 2: binary archive with MANIFEST + ZBC[n] sections (see docs/design/compilation.md).
#[derive(Debug, Serialize, Deserialize)]
pub struct ZlibFile {
    pub zlib_version: [u16; 2],
    pub name: String,
    pub version: String,
    /// `"lib"` or `"exe"`.
    pub kind: ZmodKind,
    /// All public symbols across all bundled modules.
    pub exports: Vec<ZlibExport>,
    #[serde(default)]
    pub dependencies: Vec<ZlibDep>,
    /// Inline bytecode for every source file (Phase 1 JSON form).
    pub modules: Vec<ZbcFile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub entry: Option<String>,
}

impl ZlibFile {
    pub const VERSION: [u16; 2] = [0, 1];

    /// Collect all IR modules from the bundle for loading into the VM.
    pub fn all_modules(&self) -> impl Iterator<Item = &Module> {
        self.modules.iter().map(|zbc| &zbc.module)
    }
}
