/// Artifact loader: detects the format of a compiler output file by extension,
/// deserialises it, and returns a merged `Module` plus an optional entry-point hint.
///
/// Supported formats:
///   `.z42ir.json` — legacy raw Module JSON (Phase 1 debug format, always supported)
///   `.zbc`        — ZbcFile envelope  (single source file)
///   `.zpkg`       — ZpkgFile          (project package; indexed or packed)
use std::path::{Path, PathBuf};

use anyhow::{bail, Context, Result};

use crate::bytecode::Module;
use crate::metadata::formats::{
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
    /// Resolved dependency list from the zpkg manifest (empty for .zbc / .z42ir.json).
    pub dependencies: Vec<ZpkgDep>,
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
    Ok(LoadedArtifact { module, entry_hint: None, dependencies: vec![] })
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
    // .zbc has no entry field; entry resolution falls back to Vm heuristics.
    Ok(LoadedArtifact { module: zbc.module, entry_hint: None, dependencies: vec![] })
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
    Ok(LoadedArtifact { module, entry_hint: zpkg.entry, dependencies })
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

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn make_fake_zpkg(dir: &Path, filename: &str, namespaces: &[&str]) {
        let content = serde_json::json!({
            "name": "test",
            "version": "0.1.0",
            "kind": "lib",
            "mode": "indexed",
            "namespaces": namespaces,
            "exports": [],
            "dependencies": [],
            "files": [],
            "modules": []
        });
        let path = dir.join(filename);
        std::fs::write(path, content.to_string()).expect("write test zpkg");
    }

    fn make_fake_zbc(dir: &Path, filename: &str, namespace: &str) {
        use crate::metadata::formats::{ZBC_MAGIC, ZBC_VERSION};
        // Minimal binary zbc: header (16 bytes) + NSPC section
        let ns_bytes = namespace.as_bytes();
        let ns_len = ns_bytes.len() as u16;
        let sec_len = (2 + ns_bytes.len()) as u32;

        let mut data: Vec<u8> = Vec::new();
        // Header: magic[4] + major[2] + minor[2] + flags[2] + reserved[6]
        data.extend_from_slice(&ZBC_MAGIC);
        data.extend_from_slice(&ZBC_VERSION[0].to_le_bytes());
        data.extend_from_slice(&ZBC_VERSION[1].to_le_bytes());
        data.extend_from_slice(&0u16.to_le_bytes()); // flags = 0 (full)
        data.extend_from_slice(&[0u8; 6]);           // reserved
        // NSPC section: tag[4] + len[4] + u16(ns_len) + ns_bytes
        data.extend_from_slice(b"NSPC");
        data.extend_from_slice(&sec_len.to_le_bytes());
        data.extend_from_slice(&ns_len.to_le_bytes());
        data.extend_from_slice(ns_bytes);

        let path = dir.join(filename);
        std::fs::write(path, &data).expect("write test zbc");
    }

    /// resolve_namespace with empty paths returns Ok(None)
    #[test]
    fn test_resolve_namespace_empty_paths() {
        let result = resolve_namespace("z42.io", &[], &[]);
        assert!(result.is_ok());
        assert!(result.unwrap().is_none());
    }

    /// Two zpkg files in the same libs tier providing the same namespace → error
    #[test]
    fn test_resolve_namespace_ambiguous_same_tier() {
        let tmp = std::env::temp_dir().join(format!("z42_test_{}", std::process::id()));
        std::fs::create_dir_all(&tmp).unwrap();

        make_fake_zpkg(&tmp, "libA.zpkg", &["z42.conflict"]);
        make_fake_zpkg(&tmp, "libB.zpkg", &["z42.conflict"]);

        let result = resolve_namespace("z42.conflict", &[], &[tmp.clone()]);
        std::fs::remove_dir_all(&tmp).ok();

        assert!(result.is_err(), "expected ambiguous namespace error");
        let msg = result.unwrap_err().to_string();
        assert!(msg.contains("AmbiguousNamespaceError"), "error message: {msg}");
        assert!(msg.contains("z42.conflict"), "error message: {msg}");
    }

    /// A zbc in module_paths and a zpkg in libs_paths both provide the same namespace
    /// → zbc (module_paths) wins.
    #[test]
    fn test_resolve_namespace_cross_tier_override() {
        let tmp = std::env::temp_dir().join(format!("z42_test_ct_{}", std::process::id()));
        let zbc_dir  = tmp.join("modules");
        let zpkg_dir = tmp.join("libs");
        std::fs::create_dir_all(&zbc_dir).unwrap();
        std::fs::create_dir_all(&zpkg_dir).unwrap();

        make_fake_zbc(&zbc_dir, "mymod.zbc", "z42.shared");
        make_fake_zpkg(&zpkg_dir, "mylib.zpkg", &["z42.shared"]);

        let result = resolve_namespace("z42.shared", &[zbc_dir.clone()], &[zpkg_dir.clone()]);
        std::fs::remove_dir_all(&tmp).ok();

        assert!(result.is_ok(), "unexpected error: {:?}", result.err());
        let path = result.unwrap().expect("expected Some(path)");
        assert_eq!(
            path.parent().unwrap(),
            zbc_dir.as_path(),
            "expected zbc from module_paths to win over zpkg in libs_paths"
        );
        assert_eq!(path.extension().and_then(|e| e.to_str()), Some("zbc"));
    }
}
