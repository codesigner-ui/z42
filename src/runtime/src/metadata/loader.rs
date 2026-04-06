/// Artifact loader: detects the format of a compiler output file by extension,
/// deserialises it, and returns a merged `Module` plus an optional entry-point hint.
///
/// Supported formats:
///   `.z42ir.json` — legacy raw Module JSON (Phase 1 debug format, always supported)
///   `.zbc`        — ZbcFile envelope  (single source file)
///   `.zpkg`       — ZpkgFile          (project package; indexed or packed)
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};

use super::bytecode::Module;
use super::formats::{
    read_zbc_namespace, zbc_is_stripped, ZBC_MAGIC, ZbcFile, ZpkgDep, ZpkgFile, ZpkgMode,
};
use crate::metadata::merge::merge_modules;

/// Result of loading a compiler artifact.
pub struct LoadedArtifact {
    /// The merged, flat IR module ready for the VM.
    pub module: Module,
    /// Entry-point function name from the artifact's metadata, if present.
    /// Falls back to the Vm's own lookup logic when `None`.
    pub entry_hint: Option<String>,
    /// Resolved dependency list from the zpkg manifest (empty for .z42ir.json).
    pub dependencies: Vec<ZpkgDep>,
    /// Namespace prefixes extracted from ZbcFile.imports (e.g. ["z42.core", "z42.io"]).
    /// Populated by load_zbc; used by main.rs to load the corresponding zpkgs.
    pub import_namespaces: Vec<String>,
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
    Ok(LoadedArtifact { module, entry_hint: None, dependencies: vec![], import_namespaces: vec![] })
}

/// `.zbc`: `ZbcFile` envelope → extract inner `Module`.
/// Also performs a major-version compatibility check.
/// Returns an error if the file is a stripped zbc (must be loaded via zpkg index).
fn load_zbc(path: &str) -> Result<LoadedArtifact> {
    let raw = std::fs::read(path).with_context(|| format!("cannot read `{path}`"))?;

    // 1c.4: Guard against loading stripped zbc directly.
    if raw.len() >= 4 && &raw[0..4] == ZBC_MAGIC {
        if zbc_is_stripped(&raw) {
            bail!(
                "cannot load stripped zbc directly: `{path}`; \
                 stripped zbcs live in .cache/ and must be loaded via zpkg index"
            );
        }
        // Binary zbc (v0.2+) — not yet fully supported for direct load; fall through to JSON parse.
        // TODO(M-binary): implement binary zbc loading here once binary format is the default.
    }

    let json = String::from_utf8(raw).with_context(|| format!("cannot read `{path}` as UTF-8"))?;
    let zbc: ZbcFile = serde_json::from_str(&json)
        .with_context(|| format!("cannot parse .zbc JSON in `{path}`"))?;
    check_zbc_version(&zbc, path)?;
    // Extract unique namespace prefixes from the import table for dependency resolution.
    let import_namespaces = extract_import_namespaces(&zbc.imports);
    // .zbc has no entry field; entry resolution falls back to Vm heuristics.
    Ok(LoadedArtifact { module: zbc.module, entry_hint: None, dependencies: vec![], import_namespaces })
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

    let dependencies = zpkg.dependencies;
    let module = merge_modules(modules)
        .with_context(|| format!("merging modules from `{path}`"))?;
    Ok(LoadedArtifact { module, entry_hint: zpkg.entry, dependencies, import_namespaces: vec![] })
}

// ── Namespace resolution ──────────────────────────────────────────────────────

/// Resolve which file provides a given namespace.
///
/// Search order (high → low priority):
///   1. `module_paths`: scan `.zbc` files (binary, read namespace from header)
///   2. `libs_paths`:   scan `.zpkg` files (JSON, read `namespaces` field)
///
/// Within the same tier, having two files that provide the **same** namespace
/// is an error (`AmbiguousNamespaceError`).  Cross-tier override (zbc wins over
/// zpkg) is valid and silent.
///
/// Returns `Ok(None)` if no file provides the namespace.
pub fn resolve_namespace(
    ns: &str,
    module_paths: &[PathBuf],
    libs_paths: &[PathBuf],
) -> Result<Option<PathBuf>> {
    // Tier 1: module_paths (.zbc files)
    let zbc_match = find_namespace_in_zbc_dirs(ns, module_paths)?;
    if zbc_match.is_some() {
        return Ok(zbc_match);
    }

    // Tier 2: libs_paths (.zpkg files)
    let zpkg_match = find_namespace_in_zpkg_dirs(ns, libs_paths)?;
    Ok(zpkg_match)
}

/// Scan directories for `.zbc` files whose binary NSPC header matches `ns`.
/// Returns an error if two files in the same set both provide `ns`.
fn find_namespace_in_zbc_dirs(ns: &str, dirs: &[PathBuf]) -> Result<Option<PathBuf>> {
    let mut found: Option<PathBuf> = None;
    for dir in dirs {
        let entries = match std::fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("zbc") {
                continue;
            }
            let data = match std::fs::read(&path) {
                Ok(d) => d,
                Err(_) => continue,
            };
            // Only inspect binary zbc files (starts with ZBC_MAGIC)
            if data.len() < 4 || &data[0..4] != ZBC_MAGIC {
                continue;
            }
            let file_ns = match read_zbc_namespace(&data) {
                Ok(n) => n,
                Err(_) => continue,
            };
            if file_ns == ns {
                if let Some(ref prev) = found {
                    bail!(
                        "AmbiguousNamespaceError: namespace '{}' provided by both '{}' and '{}'",
                        ns,
                        prev.display(),
                        path.display()
                    );
                }
                found = Some(path);
            }
        }
    }
    Ok(found)
}

/// Scan directories for `.zpkg` files whose `namespaces` field contains `ns`.
/// Returns an error if two files in the same set both provide `ns`.
fn find_namespace_in_zpkg_dirs(ns: &str, dirs: &[PathBuf]) -> Result<Option<PathBuf>> {
    let mut found: Option<PathBuf> = None;
    for dir in dirs {
        let entries = match std::fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("zpkg") {
                continue;
            }
            let text = match std::fs::read_to_string(&path) {
                Ok(t) => t,
                Err(_) => continue,
            };
            let pkg: ZpkgFile = match serde_json::from_str(&text) {
                Ok(p) => p,
                Err(_) => continue,
            };
            if pkg.namespaces.iter().any(|n| n == ns) {
                if let Some(ref prev) = found {
                    bail!(
                        "AmbiguousNamespaceError: namespace '{}' provided by both '{}' and '{}'",
                        ns,
                        prev.display(),
                        path.display()
                    );
                }
                found = Some(path);
            }
        }
    }
    Ok(found)
}

// ── Helpers ────────────────────────────────────────────────────────────────────

/// Extract unique namespace prefixes from a list of import symbol names.
///
/// Each import is a fully-qualified symbol like `"z42.core.String.Contains"`.
/// This function infers the package namespace from the first two dot-separated
/// components (e.g. `"z42.core"`), deduplicates, and returns the result.
///
/// Single-component names (unlikely but defensive) are returned as-is.
pub fn extract_import_namespaces(imports: &[String]) -> Vec<String> {
    let mut seen = std::collections::HashSet::new();
    let mut result = Vec::new();
    for import in imports {
        // Namespace = first two components, or the whole string if fewer than two dots.
        let ns = match import.find('.') {
            None => import.as_str(),
            Some(first_dot) => match import[first_dot + 1..].find('.') {
                None => import.as_str(),  // only one dot → use full name
                Some(rel) => &import[..first_dot + 1 + rel],
            },
        };
        if seen.insert(ns.to_owned()) {
            result.push(ns.to_owned());
        }
    }
    result
}

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

#[cfg(test)]
#[path = "loader_tests.rs"]
mod tests;
