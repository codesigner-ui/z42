/// Artifact loader: detects the format of a compiler output file by extension,
/// deserialises it (binary only), and returns a merged `Module` plus metadata.
///
/// Supported formats:
///   `.zbc`  — ZbcFile binary (single source file, full mode)
///   `.zpkg` — ZpkgFile binary (project package; packed mode only)
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::{bail, Context, Result};

use super::bytecode::Module;
use super::formats::{ZpkgDep, ZBC_MAGIC, ZPKG_MAGIC};
use super::merge::merge_modules;
use super::types::{FieldSlot, TypeDesc};
use super::zbc_reader::{read_zbc, read_zpkg_meta, read_zpkg_modules, read_zpkg_namespaces};

/// Result of loading a compiler artifact.
pub struct LoadedArtifact {
    /// The merged, flat IR module ready for the VM.
    pub module: Module,
    /// Entry-point function name from the artifact's metadata, if present.
    pub entry_hint: Option<String>,
    /// Resolved dependency list from the zpkg manifest (empty for .zbc).
    pub dependencies: Vec<ZpkgDep>,
    /// Namespace prefixes extracted from the import table (populated by load_zbc).
    /// Used by main.rs to load the corresponding zpkgs.
    pub import_namespaces: Vec<String>,
}

/// Load a compiler output artifact from `path`, returning a `LoadedArtifact`.
///
/// Format is determined by file extension (case-insensitive):
/// - `.zbc`  → binary zbc (full mode)
/// - `.zpkg` → binary zpkg (packed mode)
pub fn load_artifact(path: &str) -> Result<LoadedArtifact> {
    match Path::new(path).extension().and_then(|e| e.to_str()) {
        Some("zbc")  => load_zbc(path),
        Some("zpkg") => load_zpkg(path),
        ext => bail!(
            "unrecognised artifact extension {:?} in `{}`; expected .zbc or .zpkg",
            ext, path
        ),
    }
}

// ── Format-specific loaders ───────────────────────────────────────────────────

fn load_zbc(path: &str) -> Result<LoadedArtifact> {
    let raw = std::fs::read(path).with_context(|| format!("cannot read `{path}`"))?;

    if raw.len() < 4 || &raw[0..4] != ZBC_MAGIC {
        bail!("not a binary zbc file: `{path}`; expected ZBC magic bytes");
    }

    let mut module = read_zbc(&raw)
        .with_context(|| format!("cannot parse binary zbc `{path}`"))?;

    build_type_registry(&mut module);
    build_block_indices(&mut module);
    build_func_index(&mut module);

    // Extract import namespaces from the module's ConstStr / Call instructions
    // (approximation: namespace = first two components of any external call target)
    let import_namespaces = extract_import_namespaces_from_module(&module);

    Ok(LoadedArtifact {
        module,
        entry_hint: None,
        dependencies: vec![],
        import_namespaces,
    })
}

fn load_zpkg(path: &str) -> Result<LoadedArtifact> {
    let raw = std::fs::read(path).with_context(|| format!("cannot read `{path}`"))?;

    if raw.len() < 4 || &raw[0..4] != ZPKG_MAGIC {
        bail!("not a binary zpkg file: `{path}`; expected ZPK magic bytes");
    }

    let meta = read_zpkg_meta(&raw)
        .with_context(|| format!("cannot read zpkg metadata from `{path}`"))?;

    let module_pairs = read_zpkg_modules(&raw)
        .with_context(|| format!("cannot load modules from `{path}`"))?;

    let modules: Vec<Module> = module_pairs.into_iter().map(|(m, _)| m).collect();
    let mut module = merge_modules(modules)
        .with_context(|| format!("merging modules from `{path}`"))?;

    build_type_registry(&mut module);
    build_block_indices(&mut module);
    build_func_index(&mut module);

    Ok(LoadedArtifact {
        module,
        entry_hint: meta.entry,
        dependencies: meta.dependencies,
        import_namespaces: vec![],
    })
}

// ── Namespace resolution ──────────────────────────────────────────────────────

/// Resolve which file provides a given namespace.
///
/// Search order (high → low priority):
///   1. `module_paths`: scan `.zbc` files (binary, read namespace from header)
///   2. `libs_paths`:   scan `.zpkg` files (binary, read NSPC section)
pub fn resolve_namespace(
    ns: &str,
    module_paths: &[PathBuf],
    libs_paths: &[PathBuf],
) -> Result<Option<PathBuf>> {
    let zbc_match = find_namespace_in_zbc_dirs(ns, module_paths)?;
    if zbc_match.is_some() {
        return Ok(zbc_match);
    }
    find_namespace_in_zpkg_dirs(ns, libs_paths)
}

fn find_namespace_in_zbc_dirs(ns: &str, dirs: &[PathBuf]) -> Result<Option<PathBuf>> {
    let mut found: Option<PathBuf> = None;
    for dir in dirs {
        let entries = match std::fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("zbc") { continue; }
            let data = match std::fs::read(&path) { Ok(d) => d, Err(_) => continue };
            if data.len() < 4 || &data[0..4] != ZBC_MAGIC { continue; }
            let file_ns = match read_zbc_namespace(&data) { Ok(n) => n, Err(_) => continue };
            if file_ns == ns {
                if let Some(ref prev) = found {
                    bail!(
                        "AmbiguousNamespaceError: namespace '{}' provided by both '{}' and '{}'",
                        ns, prev.display(), path.display()
                    );
                }
                found = Some(path);
            }
        }
    }
    Ok(found)
}

fn find_namespace_in_zpkg_dirs(ns: &str, dirs: &[PathBuf]) -> Result<Option<PathBuf>> {
    let mut found: Option<PathBuf> = None;
    for dir in dirs {
        let entries = match std::fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => continue,
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("zpkg") { continue; }
            let data = match std::fs::read(&path) { Ok(d) => d, Err(_) => continue };
            if data.len() < 4 || &data[0..4] != ZPKG_MAGIC { continue; }
            let namespaces = match read_zpkg_namespaces(&data) { Ok(v) => v, Err(_) => continue };
            if namespaces.iter().any(|n| n == ns) {
                if let Some(ref prev) = found {
                    bail!(
                        "AmbiguousNamespaceError: namespace '{}' provided by both '{}' and '{}'",
                        ns, prev.display(), path.display()
                    );
                }
                found = Some(path);
            }
        }
    }
    Ok(found)
}

// ── Helpers ────────────────────────────────────────────────────────────────────

/// Reads the namespace from a binary zbc buffer (NSPC section fast-path).
pub fn read_zbc_namespace(data: &[u8]) -> Result<String> {
    use super::formats::ZBC_MAGIC;
    if data.len() < 16 { bail!("zbc buffer too short ({} bytes)", data.len()) }
    if &data[0..4] != ZBC_MAGIC { bail!("not a zbc file (bad magic)") }

    let sec_count = u16::from_le_bytes([data[10], data[11]]);
    let dir = super::zbc_reader::read_directory_pub(data, sec_count)?;

    match dir.get(b"NSPC") {
        None => Ok(String::new()),
        Some(&(off, size)) => {
            if off + size > data.len() { bail!("NSPC section out of bounds") }
            let sec = &data[off..off + size];
            if sec.len() < 2 { return Ok(String::new()); }
            let len = u16::from_le_bytes([sec[0], sec[1]]) as usize;
            if len == 0 || sec.len() < 2 + len { return Ok(String::new()); }
            Ok(std::str::from_utf8(&sec[2..2 + len])?.to_owned())
        }
    }
}

/// Extract unique namespace prefixes from a module's external calls.
/// Namespace = first two dot-separated components of a Call target not defined locally.
fn extract_import_namespaces_from_module(module: &Module) -> Vec<String> {
    use super::bytecode::Instruction;
    let defined: std::collections::HashSet<&str> =
        module.functions.iter().map(|f| f.name.as_str()).collect();
    let mut seen = std::collections::HashSet::new();
    let mut result = Vec::new();
    for func in &module.functions {
        for block in &func.blocks {
            for instr in &block.instructions {
                let target = match instr {
                    Instruction::Call { func, .. }    if !defined.contains(func.as_str()) => func,
                    Instruction::Builtin { name, .. } => name,
                    _ => continue,
                };
                let ns = infer_namespace(target);
                if seen.insert(ns.to_owned()) {
                    result.push(ns.to_owned());
                }
            }
        }
    }
    result
}

/// Infer the namespace from a fully-qualified function name.
/// Returns the first two dot-separated components, or the whole name.
pub fn extract_import_namespaces(imports: &[String]) -> Vec<String> {
    let mut seen = std::collections::HashSet::new();
    let mut result = Vec::new();
    for import in imports {
        let ns = infer_namespace(import);
        if seen.insert(ns.to_owned()) { result.push(ns.to_owned()); }
    }
    result
}

fn infer_namespace(name: &str) -> &str {
    match name.find('.') {
        None => name,
        Some(first) => match name[first + 1..].find('.') {
            None => name,
            Some(rel) => &name[..first + 1 + rel],
        },
    }
}

// ── TypeDesc registry ─────────────────────────────────────────────────────────

/// Pre-build a `TypeDesc` for every class in `module.classes` and store the
/// results in `module.type_registry`.
///
/// Algorithm (CoreCLR-inspired):
///   1. Topological sort: each class is processed after its base class.
///   2. Field slots: base fields first (already in base TypeDesc), then derived.
///   3. vtable: start with base vtable, override entries where derived defines
///      the same method name, append new methods at the end.
pub fn build_type_registry(module: &mut Module) {
    let order = topo_sort_classes(module);
    let mut registry: HashMap<String, Arc<TypeDesc>> = HashMap::new();

    for class_name in &order {
        let desc = match module.classes.iter().find(|c| &c.name == class_name) {
            Some(d) => d,
            None    => continue,
        };

        // ── Field slots: base first, then derived (no duplicate names) ────
        let mut fields: Vec<FieldSlot> = desc.base_class
            .as_deref()
            .and_then(|b| registry.get(b))
            .map(|td| td.fields.clone())
            .unwrap_or_default();

        for f in &desc.fields {
            if !fields.iter().any(|s| s.name == f.name) {
                fields.push(FieldSlot { name: f.name.clone() });
            }
        }

        let field_index: HashMap<String, usize> = fields.iter().enumerate()
            .map(|(i, f)| (f.name.clone(), i))
            .collect();

        // ── vtable: start from base, override/append for this class ───────
        let (mut vtable, mut vtable_index): (Vec<(String, String)>, HashMap<String, usize>) =
            desc.base_class
                .as_deref()
                .and_then(|b| registry.get(b))
                .map(|td| (td.vtable.clone(), td.vtable_index.clone()))
                .unwrap_or_default();

        // Scan module functions for methods belonging to this class.
        let prefix = format!("{}.", class_name);
        for func in &module.functions {
            if !func.name.starts_with(&prefix) { continue; }
            let method = &func.name[prefix.len()..];
            // Skip constructors (same name as class simple name) and __static_init__
            let simple_name = class_name.split('.').next_back().unwrap_or(class_name.as_str());
            if method == simple_name || method.starts_with("__") { continue; }
            // Arity-overloaded names (Method$N) share the base slot with Method
            let base_method = method.split('$').next().unwrap_or(method);
            if let Some(&slot) = vtable_index.get(base_method) {
                vtable[slot] = (base_method.to_string(), func.name.clone());
            } else {
                let slot = vtable.len();
                vtable_index.insert(base_method.to_string(), slot);
                vtable.push((base_method.to_string(), func.name.clone()));
            }
        }

        registry.insert(class_name.clone(), Arc::new(TypeDesc {
            name: class_name.clone(),
            base_name: desc.base_class.clone(),
            fields,
            field_index,
            vtable,
            vtable_index,
        }));
    }

    module.type_registry = registry;
}

/// Precompute block label → index mapping for all functions in the module.
/// This eliminates the O(n) HashMap construction in every exec_function call.
pub fn build_block_indices(module: &mut Module) {
    for func in &mut module.functions {
        func.block_index = func.blocks
            .iter()
            .enumerate()
            .map(|(i, b)| (b.label.clone(), i))
            .collect();
    }
}

/// Precompute function name → index mapping for O(1) call dispatch.
pub fn build_func_index(module: &mut Module) {
    module.func_index = module.functions
        .iter()
        .enumerate()
        .map(|(i, f)| (f.name.clone(), i))
        .collect();
}

/// Return class names in topological order (base before derived).
fn topo_sort_classes(module: &Module) -> Vec<String> {
    let mut visited: std::collections::HashSet<String> = std::collections::HashSet::new();
    let mut order: Vec<String> = Vec::new();

    fn visit(
        name: &str,
        module: &Module,
        visited: &mut std::collections::HashSet<String>,
        order: &mut Vec<String>,
    ) {
        if visited.contains(name) { return; }
        visited.insert(name.to_string());
        if let Some(desc) = module.classes.iter().find(|c| c.name == name) {
            if let Some(base) = &desc.base_class {
                visit(base, module, visited, order);
            }
        }
        order.push(name.to_string());
    }

    for cls in &module.classes {
        visit(&cls.name, module, &mut visited, &mut order);
    }
    order
}

#[cfg(test)]
#[path = "loader_tests.rs"]
mod tests;
