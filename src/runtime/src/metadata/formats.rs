/// Data structures for z42 compiler output formats (Phase 1: JSON).
///
/// Mirrors the C# types in `z42.IR/PackageTypes.cs` exactly.
/// See `docs/design/compilation.md` for the full spec.
use serde::{Deserialize, Serialize};

use crate::bytecode::Module;

// ── Magic bytes (reserved for Phase 2 binary format) ─────────────────────────

/// Magic bytes for `.zbc` binary format (Phase 2): `"ZBC\0"`
pub const ZBC_MAGIC: [u8; 4] = [0x5A, 0x42, 0x43, 0x00];

/// Magic bytes for `.zlib` binary format (Phase 2): `"ZLB\0"`
pub const ZLIB_MAGIC: [u8; 4] = [0x5A, 0x4C, 0x42, 0x00];

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
    /// Local path or registry URL pointing to a `.zlib`.
    pub path: String,
    /// SemVer constraint, e.g. `">=0.1"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version: Option<String>,
}

/// Project kind — determines whether an `entry` function is required.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ZmodKind {
    Lib,
    Exe,
}

/// The `.zmod` manifest — project-level index of `.zbc` files and dependencies.
///
/// Analogy: C# `.csproj` (declares source files, entry point, deps).
///
/// Always JSON; designed to be VCS-friendly (no binary blobs).
#[derive(Debug, Serialize, Deserialize)]
pub struct ZmodManifest {
    pub zmod_version: [u16; 2],
    /// Project / library name.
    pub name: String,
    pub version: String,
    /// `"lib"` (no entry point) or `"exe"` (has `entry`).
    pub kind: ZmodKind,
    /// All source files belonging to this project.
    pub files: Vec<ZmodFileEntry>,
    /// External `.zlib` dependencies.
    #[serde(default)]
    pub dependencies: Vec<ZmodDep>,
    /// Qualified entry-point function for `kind = exe`, e.g. `"Hello.Main"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub entry: Option<String>,
}

impl ZmodManifest {
    pub const VERSION: [u16; 2] = [0, 1];

    /// Returns the entries whose recorded `source_hash` differs from the
    /// provided current hash list.  Caller supplies `(source_path, current_hash)` pairs.
    pub fn stale_files<'a>(&'a self, current_hashes: &[(&str, &str)]) -> Vec<&'a ZmodFileEntry> {
        self.files
            .iter()
            .filter(|f| {
                current_hashes
                    .iter()
                    .find(|(src, _)| *src == f.source)
                    .map(|(_, hash)| *hash != f.source_hash)
                    .unwrap_or(true)
            })
            .collect()
    }
}

// ── .zlib — assembly / library bundle ─────────────────────────────────────────

/// An exported symbol entry inside a `.zlib`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZlibExport {
    /// Fully-qualified name, e.g. `"Demo.Greet.Greet"`.
    pub symbol: String,
    /// `"func"`, `"type"`, or `"const"`.
    pub kind: String,
}

/// External dependency declared in a `.zlib`.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZlibDep {
    pub name: String,
    pub version: String,
}

/// A `.zlib` assembly — bundles all `.zbc` files of a project into one
/// self-contained, distributable file.
///
/// Analogy: C# `.dll` = PE envelope + metadata tables + IL sections.
///
/// Phase 1: JSON (all `ZbcFile`s inlined under `modules`).
/// Phase 2: binary archive — `ZLIB_MAGIC` + MANIFEST + ZBC[n] sections.
#[derive(Debug, Serialize, Deserialize)]
pub struct ZlibFile {
    pub zlib_version: [u16; 2],
    pub name: String,
    pub version: String,
    /// `"lib"` or `"exe"`.
    pub kind: ZmodKind,
    /// All public symbols across every bundled module.
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
}
