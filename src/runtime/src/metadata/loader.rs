/// Artifact loader: detects the format of a compiler output file by extension,
/// deserialises it, and returns a merged `Module` plus an optional entry-point hint.
///
/// Supported formats:
///   `.z42ir.json` — legacy raw Module JSON (Phase 1 debug format, always supported)
///   `.zbc`        — ZbcFile envelope  (single source file)
///   `.zmod`       — ZmodManifest      (multi-file project; references .zbc on disk)
///   `.zbin`       — ZbinFile          (self-contained bundle; .zbc inlined; exe or lib)
use std::path::Path;

use anyhow::{bail, Context, Result};

use crate::bytecode::Module;
use crate::metadata::formats::{ZbcFile, ZbinFile, ZmodManifest};
use crate::metadata::merge::merge_modules;

/// Result of loading a compiler artifact.
pub struct LoadedArtifact {
    /// The merged, flat IR module ready for the VM.
    pub module: Module,
    /// Entry-point function name from the artifact's metadata, if present.
    /// Falls back to the Vm's own lookup logic when `None`.
    pub entry_hint: Option<String>,
}

/// Load a compiler output artifact from `path`, returning a `LoadedArtifact`.
///
/// Format is determined by file extension (case-insensitive):
/// - `.z42ir.json` / `.json` → raw `Module` (legacy)
/// - `.zbc`                  → `ZbcFile`
/// - `.zmod`                 → `ZmodManifest` (reads sibling `.zbc` files from disk)
/// - `.zbin`                 → `ZbinFile` (exe or lib bundle)
pub fn load_artifact(path: &str) -> Result<LoadedArtifact> {
    let lower = path.to_lowercase();

    if lower.ends_with(".z42ir.json") || (lower.ends_with(".json") && !lower.ends_with(".zbc")) {
        load_legacy_ir(path)
    } else {
        match Path::new(path).extension().and_then(|e| e.to_str()) {
            Some("zbc")  => load_zbc(path),
            Some("zmod") => load_zmod(path),
            Some("zbin") => load_zbin(path),
            ext => bail!(
                "unrecognised artifact extension {:?} in `{}`; \
                 expected .z42ir.json, .zbc, .zmod, or .zbin",
                ext, path
            ),
        }
    }
}

// ── Format-specific loaders ───────────────────────────────────────────────────

/// Legacy `.z42ir.json`: a bare `Module` with no envelope.
fn load_legacy_ir(path: &str) -> Result<LoadedArtifact> {
    let json = read_file(path)?;
    let module: Module = serde_json::from_str(&json)
        .with_context(|| format!("cannot parse IR JSON in `{path}`"))?;
    Ok(LoadedArtifact { module, entry_hint: None })
}

/// `.zbc`: `ZbcFile` envelope → extract inner `Module`.
/// Also performs a major-version compatibility check.
fn load_zbc(path: &str) -> Result<LoadedArtifact> {
    let json = read_file(path)?;
    let zbc: ZbcFile = serde_json::from_str(&json)
        .with_context(|| format!("cannot parse .zbc JSON in `{path}`"))?;
    check_zbc_version(&zbc, path)?;
    // .zbc has no entry field; entry resolution falls back to Vm heuristics.
    Ok(LoadedArtifact { module: zbc.module, entry_hint: None })
}

/// `.zmod`: read manifest, load each referenced `.zbc` file, merge modules.
fn load_zmod(path: &str) -> Result<LoadedArtifact> {
    let json = read_file(path)?;
    let manifest: ZmodManifest = serde_json::from_str(&json)
        .with_context(|| format!("cannot parse .zmod JSON in `{path}`"))?;

    let base = Path::new(path)
        .parent()
        .unwrap_or(Path::new("."));

    let mut modules = Vec::with_capacity(manifest.files.len());
    for entry in &manifest.files {
        let zbc_path = base.join(&entry.bytecode);
        let zbc_str = zbc_path.to_string_lossy();
        let zbc_json = read_file(&zbc_str)
            .with_context(|| format!("loading .zbc `{}` referenced from `{path}`", zbc_str))?;
        let zbc: ZbcFile = serde_json::from_str(&zbc_json)
            .with_context(|| format!("cannot parse .zbc `{}`", zbc_str))?;
        check_zbc_version(&zbc, &zbc_str)?;
        modules.push(zbc.module);
    }

    let module = merge_modules(modules)
        .with_context(|| format!("merging modules from `{path}`"))?;
    Ok(LoadedArtifact { module, entry_hint: manifest.entry })
}

/// `.zbin`: all `ZbcFile`s are inlined; extract and merge.
/// Works for both exe (has entry) and lib (no entry) bundles.
fn load_zbin(path: &str) -> Result<LoadedArtifact> {
    let json = read_file(path)?;
    let zbin: ZbinFile = serde_json::from_str(&json)
        .with_context(|| format!("cannot parse .zbin JSON in `{path}`"))?;

    check_zbin_version(&zbin, path)?;

    let modules: Vec<Module> = zbin
        .modules
        .into_iter()
        .enumerate()
        .map(|(i, zbc)| {
            check_zbc_version(&zbc, &format!("{path}#module[{i}]"))?;
            Ok(zbc.module)
        })
        .collect::<Result<_>>()?;

    let module = merge_modules(modules)
        .with_context(|| format!("merging modules from `{path}`"))?;
    Ok(LoadedArtifact { module, entry_hint: zbin.entry })
}

// ── Helpers ────────────────────────────────────────────────────────────────────

fn read_file(path: &str) -> Result<String> {
    std::fs::read_to_string(path).with_context(|| format!("cannot read `{path}`"))
}

fn check_zbc_version(zbc: &ZbcFile, path: &str) -> Result<()> {
    if zbc.zbc_version[0] > ZbcFile::VERSION[0] {
        bail!(
            "unsupported .zbc major version {} in `{path}` (this VM supports <= {})",
            zbc.zbc_version[0],
            ZbcFile::VERSION[0]
        );
    }
    Ok(())
}

fn check_zbin_version(zbin: &ZbinFile, path: &str) -> Result<()> {
    if zbin.zbin_version[0] > ZbinFile::VERSION[0] {
        bail!(
            "unsupported .zbin major version {} in `{path}` (this VM supports <= {})",
            zbin.zbin_version[0],
            ZbinFile::VERSION[0]
        );
    }
    Ok(())
}
