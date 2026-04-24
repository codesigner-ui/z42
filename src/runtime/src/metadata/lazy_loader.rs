/// Lazy dependency loader (zpkg-based, C# assembly model).
///
/// The VM eagerly loads only `z42.core` at startup. Other stdlib/third-party
/// zpkgs are loaded on demand when the interpreter encounters a `Call` or
/// `ObjNew` against an undefined function/type whose namespace matches a
/// declared-but-not-loaded zpkg.
///
/// ## Triggering (Decision 1: strategy C + fallback B)
///
///   1. Extract namespace prefix from `func_name` / `class_name`
///   2. Route to candidate zpkgs whose exported `namespaces` metadata
///      contains that prefix (precise routing, like C# CLR AssemblyRef →
///      TypeRef lookup)
///   3. Fallback: if strategy C matches nothing, iterate every declared-but-
///      -not-loaded zpkg until the target resolves or the set is exhausted
///   4. Transitive `ZpkgDep`s are unfolded into the declared set on load
///      (Decision 4 cycle-safe via pre-insert colouring)
///
/// Multiple zpkgs may legitimately declare the same namespace — the lookup
/// visits them one by one and `first-wins` on function/type name collisions
/// (Decision 6).
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::Result;

use super::bytecode::{Function, Instruction};
use super::loader::load_artifact;
use super::types::TypeDesc;
use super::zbc_reader::read_zpkg_meta;

// Per-thread lazy loader state. Initialised by `install(...)` at VM startup.
thread_local! {
    static STATE: RefCell<Option<LazyLoader>> = const { RefCell::new(None) };
}

// ── Public API ────────────────────────────────────────────────────────────────

/// A declared-but-not-yet-loaded zpkg. Records the information needed to
/// route a `Call` / `ObjNew` miss to the right zpkg for loading.
#[derive(Debug, Clone)]
pub struct ZpkgCandidate {
    /// Absolute path to the zpkg file.
    pub file_path: PathBuf,
    /// Namespaces exported by this zpkg (from its NSPC section).
    pub namespaces: Vec<String>,
}

impl ZpkgCandidate {
    /// Build a candidate by reading the zpkg metadata from disk.
    pub fn build(libs_dir: &Path, file_name: &str) -> Result<Self> {
        let file_path = libs_dir.join(file_name);
        let data = std::fs::read(&file_path)?;
        let meta = read_zpkg_meta(&data)?;
        Ok(Self {
            file_path,
            namespaces: meta.namespaces,
        })
    }
}

/// Install the lazy loader with no declared dependencies (tests / single-file
/// scripts without any stdlib references). Backwards-compatible signature.
pub fn install(libs_dir: Option<PathBuf>, main_pool_len: usize) {
    install_with_deps(libs_dir, main_pool_len, Vec::new(), Vec::new());
}

/// Install the lazy loader with the given declared dependencies.
///
/// `declared` — (zpkg_file_name, candidate) pairs that are referenced by the
/// main module (directly or transitively). These zpkgs may be loaded on
/// demand when the interpreter encounters an unresolved `Call` / `ObjNew`.
///
/// `initially_loaded` — zpkg file names already merged into the main module
/// at startup (e.g. `["z42.core.zpkg"]` from the implicit prelude path).
/// These are excluded from the declared-but-not-loaded candidate set.
pub fn install_with_deps(
    libs_dir: Option<PathBuf>,
    main_pool_len: usize,
    declared: Vec<(String, ZpkgCandidate)>,
    initially_loaded: Vec<String>,
) {
    STATE.with(|s| {
        *s.borrow_mut() = Some(LazyLoader::new(
            libs_dir,
            main_pool_len,
            declared,
            initially_loaded,
        ));
    });
}

/// Clear the lazy loader (used in tests to reset state between runs).
pub fn uninstall() {
    STATE.with(|s| *s.borrow_mut() = None);
}

/// Look up a function by fully-qualified name. Returns `Some(Function)` if
/// the function was found either in the already-loaded table or after
/// triggering a lazy load.
pub fn try_lookup_function(func_name: &str) -> Option<Arc<Function>> {
    STATE.with(|s| {
        let mut state = s.borrow_mut();
        let loader = state.as_mut()?;
        loader.resolve_function(func_name)
    })
}

/// Look up a type descriptor (class) by fully-qualified name.
/// L3-G4d: also triggers the zpkg load for the owning namespace so the first
/// `new Stack<int>()` on an imported generic class resolves.
pub fn try_lookup_type(class_name: &str) -> Option<Arc<TypeDesc>> {
    STATE.with(|s| {
        let mut state = s.borrow_mut();
        let loader = state.as_mut()?;
        loader.resolve_type(class_name)
    })
}

/// Resolve an "overflow" ConstStr index — one that falls past the main
/// module's string pool. Returns the merged lazy-pool string if available.
pub fn try_lookup_string(absolute_idx: usize) -> Option<String> {
    STATE.with(|s| {
        let state = s.borrow();
        let loader = state.as_ref()?;
        let rel = absolute_idx.checked_sub(loader.main_pool_len)?;
        loader.string_pool.get(rel).cloned()
    })
}

// ── State ─────────────────────────────────────────────────────────────────────

struct LazyLoader {
    libs_dir:       Option<PathBuf>,
    /// Length of the main (user) module's string pool.
    /// ConstStr indices < `main_pool_len` resolve against the main module's
    /// pool; indices >= `main_pool_len` resolve against `string_pool` below
    /// at relative offset `idx - main_pool_len`.
    main_pool_len:  usize,
    /// Aggregated string pool from all lazy-loaded zpkgs.
    string_pool:    Vec<String>,

    /// zpkg file names that have been loaded (either eagerly at startup or
    /// by a previous lazy-load). Used for de-duplication and cycle-cutting
    /// (Decision 4: pre-inserted before load to break cycles).
    loaded_zpkgs:   HashSet<String>,
    /// zpkg file names that are declared as dependencies (direct or
    /// transitive) but have not yet been loaded. Lookup candidates.
    declared_zpkgs: HashMap<String, ZpkgCandidate>,

    /// Functions loaded from lazily-resolved zpkgs, indexed by FQ name.
    /// ConstStr indices have been remapped to absolute indices.
    function_table: HashMap<String, Arc<Function>>,
    /// Type descriptors from lazily-resolved zpkgs.
    type_registry:  HashMap<String, Arc<TypeDesc>>,
}

impl LazyLoader {
    fn new(
        libs_dir: Option<PathBuf>,
        main_pool_len: usize,
        declared: Vec<(String, ZpkgCandidate)>,
        initially_loaded: Vec<String>,
    ) -> Self {
        let loaded_zpkgs: HashSet<String> = initially_loaded.into_iter().collect();
        let declared_zpkgs: HashMap<String, ZpkgCandidate> = declared
            .into_iter()
            .filter(|(k, _)| !loaded_zpkgs.contains(k))
            .collect();
        Self {
            libs_dir,
            main_pool_len,
            string_pool:    Vec::new(),
            loaded_zpkgs,
            declared_zpkgs,
            function_table: HashMap::new(),
            type_registry:  HashMap::new(),
        }
    }

    fn resolve_function(&mut self, func_name: &str) -> Option<Arc<Function>> {
        if let Some(f) = self.function_table.get(func_name) {
            return Some(Arc::clone(f));
        }
        // Strategy C: precise routing by namespace prefix
        if let Some(ns) = namespace_prefix(func_name) {
            for zpkg_file in self.candidates_for_namespace(&ns) {
                let _ = self.load_zpkg_file(&zpkg_file);
                if let Some(f) = self.function_table.get(func_name) {
                    return Some(Arc::clone(f));
                }
            }
        }
        // Fallback B: try every remaining declared-but-not-loaded zpkg
        for zpkg_file in self.remaining_declared() {
            let _ = self.load_zpkg_file(&zpkg_file);
            if let Some(f) = self.function_table.get(func_name) {
                return Some(Arc::clone(f));
            }
        }
        None
    }

    fn resolve_type(&mut self, class_name: &str) -> Option<Arc<TypeDesc>> {
        if let Some(td) = self.type_registry.get(class_name) {
            return Some(Arc::clone(td));
        }
        // Strategy C: use the class's enclosing namespace (strip last segment)
        if let Some((ns, _)) = class_name.rsplit_once('.') {
            for zpkg_file in self.candidates_for_namespace(ns) {
                let _ = self.load_zpkg_file(&zpkg_file);
                if let Some(td) = self.type_registry.get(class_name) {
                    return Some(Arc::clone(td));
                }
            }
        }
        for zpkg_file in self.remaining_declared() {
            let _ = self.load_zpkg_file(&zpkg_file);
            if let Some(td) = self.type_registry.get(class_name) {
                return Some(Arc::clone(td));
            }
        }
        None
    }

    /// Return zpkg file names from `declared_zpkgs` whose exported namespaces
    /// cover `ns` (exact match or a descendant like `Std.Collections.Generic`
    /// covering a query for `Std.Collections`).
    fn candidates_for_namespace(&self, ns: &str) -> Vec<String> {
        let ns_dot = format!("{ns}.");
        self.declared_zpkgs
            .iter()
            .filter(|(file, cand)| {
                !self.loaded_zpkgs.contains(file.as_str())
                    && cand.namespaces.iter().any(|n| n == ns || n.starts_with(&ns_dot))
            })
            .map(|(file, _)| file.clone())
            .collect()
    }

    fn remaining_declared(&self) -> Vec<String> {
        self.declared_zpkgs
            .keys()
            .filter(|f| !self.loaded_zpkgs.contains(f.as_str()))
            .cloned()
            .collect()
    }

    /// Load a zpkg file, merge its functions / types / strings, and expand
    /// its own `ZpkgDep` list into `declared_zpkgs` for future transitive
    /// lookups.
    ///
    /// Cycle-safe: inserts into `loaded_zpkgs` **before** loading the
    /// artifact, so a re-entrant call (A depends on B, B depends on A)
    /// returns immediately on the second visit.
    fn load_zpkg_file(&mut self, file_name: &str) -> Result<()> {
        if self.loaded_zpkgs.contains(file_name) {
            return Ok(());
        }
        let file_path = match self.declared_zpkgs.get(file_name) {
            Some(c) => c.file_path.clone(),
            None    => return Ok(()),
        };

        // Decision 4: pre-insert to break cycles before recursive dep expansion.
        self.loaded_zpkgs.insert(file_name.to_string());

        let path_str = file_path.to_string_lossy().into_owned();
        let artifact = load_artifact(&path_str)?;

        let offset = self.main_pool_len + self.string_pool.len();
        self.string_pool.extend(artifact.module.string_pool.iter().cloned());

        // Decision 6: first-wins on function / type name collisions.
        for mut fn_ in artifact.module.functions {
            remap_const_str(&mut fn_, offset);
            let name = fn_.name.clone();
            if self.function_table.contains_key(&name) {
                tracing::warn!(
                    "duplicate function `{name}` from zpkg `{file_name}`; keeping first-loaded"
                );
                continue;
            }
            self.function_table.insert(name, Arc::new(fn_));
        }
        for (name, desc) in artifact.module.type_registry {
            if self.type_registry.contains_key(&name) {
                tracing::warn!(
                    "duplicate type `{name}` from zpkg `{file_name}`; keeping first-loaded"
                );
                continue;
            }
            self.type_registry.insert(name, desc);
        }

        // Transitively expand `ZpkgDep` list into the declared set.
        if let Some(libs) = self.libs_dir.clone() {
            for dep in &artifact.dependencies {
                if self.loaded_zpkgs.contains(&dep.file)
                    || self.declared_zpkgs.contains_key(&dep.file)
                {
                    continue;
                }
                match ZpkgCandidate::build(&libs, &dep.file) {
                    Ok(cand) => {
                        self.declared_zpkgs.insert(dep.file.clone(), cand);
                    }
                    Err(e) => tracing::warn!(
                        "cannot read transitive dep zpkg meta `{}`: {e}", dep.file
                    ),
                }
            }
        }

        tracing::debug!("lazy-loaded zpkg `{file_name}` from {path_str}");
        Ok(())
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Rewrite all ConstStr `idx` values in a function's blocks by adding
/// `offset`, so the resulting indices point into the merged main+lazy pool.
fn remap_const_str(fn_: &mut Function, offset: usize) {
    for block in fn_.blocks.iter_mut() {
        for instr in block.instructions.iter_mut() {
            if let Instruction::ConstStr { idx, .. } = instr {
                *idx += offset as u32;
            }
        }
    }
}

/// Extract the namespace prefix from a fully-qualified function name.
/// E.g. "Std.IO.Console.WriteLine" → Some("Std.IO")
///      "Std.Assert.Equal"         → Some("Std")
///      "main"                     → None (no namespace)
fn namespace_prefix(func_name: &str) -> Option<String> {
    // A qualified function name has the form: <ns>.<Class>.<method>
    //                                         or <ns>.<func>
    // Strategy: strip the last two segments (Class.method), keep the rest.
    let dots: Vec<usize> = func_name.match_indices('.').map(|(i, _)| i).collect();
    if dots.len() < 2 {
        // "Class.method" — no explicit namespace. Use first segment as candidate.
        return dots.first().map(|&i| func_name[..i].to_string());
    }
    Some(func_name[..dots[dots.len() - 2]].to_string())
}

#[cfg(test)]
#[path = "lazy_loader_tests.rs"]
mod lazy_loader_tests;
