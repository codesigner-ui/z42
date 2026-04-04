/// Artifact loader: detects the format of a compiler output file by extension,
/// deserialises it, and returns a merged `Module` plus an optional entry-point hint.
///
/// Supported formats:
///   `.z42ir.json` — legacy raw Module JSON (Phase 1 debug format, always supported)
///   `.zbc`        — ZbcFile envelope  (single source file)
///   `.zpkg`       — ZpkgFile          (project package; indexed or packed)
use std::path::Path;

use anyhow::{bail, Context, Result};

use crate::bytecode::Module;
use crate::metadata::formats::{ZbcFile, ZpkgFile, ZpkgMode};
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
/// - `.zpkg`                 → `ZpkgFile` (indexed or packed)
pub fn load_artifact(path: &str) -> Result<LoadedArtifact> {
    let lower = path.to_lowercase();

    if lower.ends_with(".z42ir.json") || (lower.ends_with(".json") && !lower.ends_with(".zbc")) {
        load_legacy_ir(path)
    } else {
        match Path::new(path).extension().and_then(|e| e.to_str()) {
            Some("zbc")  => load_zbc(path),
            Some("zpkg") => load_zpkg(path),
            ext => bail!(
                "unrecognised artifact extension {:?} in `{}`; \
                 expected .z42ir.json, .zbc, or .zpkg",
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

/// `.zpkg`: unified project package — handles both indexed and packed modes.
fn load_zpkg(path: &str) -> Result<LoadedArtifact> {
    let json = read_file(path)?;
    let zpkg: ZpkgFile = serde_json::from_str(&json)
        .with_context(|| format!("cannot parse .zpkg JSON in `{path}`"))?;

    let modules = match zpkg.mode {
        ZpkgMode::Packed => {
            // All ZbcFiles are inlined in `modules[]`.
            zpkg.modules
                .into_iter()
                .enumerate()
                .map(|(i, zbc)| {
                    check_zbc_version(&zbc, &format!("{path}#module[{i}]"))?;
                    Ok(zbc.module)
                })
                .collect::<Result<Vec<_>>>()?
        }
        ZpkgMode::Indexed => {
            // `files[]` references .zbc paths on disk, relative to the .zpkg file.
            let base = Path::new(path)
                .parent()
                .unwrap_or(Path::new("."));

            zpkg.files
                .iter()
                .map(|entry| {
                    let zbc_path = base.join(&entry.bytecode);
                    let zbc_str = zbc_path.to_string_lossy();
                    let zbc_json = read_file(&zbc_str)
                        .with_context(|| {
                            format!("loading .zbc `{}` referenced from `{path}`", zbc_str)
                        })?;
                    let zbc: ZbcFile = serde_json::from_str(&zbc_json)
                        .with_context(|| format!("cannot parse .zbc `{}`", zbc_str))?;
                    check_zbc_version(&zbc, &zbc_str)?;
                    Ok(zbc.module)
                })
                .collect::<Result<Vec<_>>>()?
        }
    };

    let module = merge_modules(modules)
        .with_context(|| format!("merging modules from `{path}`"))?;
    Ok(LoadedArtifact { module, entry_hint: zpkg.entry })
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
