/// Lazy dependency loader.
///
/// The VM eagerly loads only `z42.core` at startup. Other stdlib/third-party
/// zpkgs are loaded on demand when the interpreter encounters a `Call` to an
/// undefined function whose fully-qualified name matches a known dependency
/// namespace.
///
/// Lookup flow on Call miss:
///   1. Extract namespace prefix from `func_name` (e.g. "Std.IO" from "Std.IO.Console.WriteLine")
///   2. Check if already loaded → if yes, the function genuinely doesn't exist
///   3. Resolve namespace → zpkg path in `libs_dir`
///   4. Load zpkg, merge its `Function`s + `TypeDesc`s into the lazy registry
///   5. Retry function lookup (caller then searches combined function table)
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::Arc;

use anyhow::Result;

use super::bytecode::Function;
use super::loader::load_artifact;
use super::types::TypeDesc;

// Per-thread lazy loader state. Initialised by `install(...)` at VM startup.
thread_local! {
    static STATE: RefCell<Option<LazyLoader>> = RefCell::new(None);
}

/// Install the lazy loader with the given libs directory.
/// Called once at VM startup after locating the stdlib libs dir.
pub fn install(libs_dir: Option<PathBuf>) {
    STATE.with(|s| *s.borrow_mut() = Some(LazyLoader::new(libs_dir)));
}

/// Clear the lazy loader (used in tests to reset state between runs).
pub fn uninstall() {
    STATE.with(|s| *s.borrow_mut() = None);
}

/// Look up a function by fully-qualified name. Returns `Some(Function)` if the
/// function was found either in an already-loaded dependency or after
/// triggering a lazy load of the zpkg that provides its namespace.
pub fn try_lookup_function(func_name: &str) -> Option<Arc<Function>> {
    STATE.with(|s| {
        let mut state = s.borrow_mut();
        let loader = state.as_mut()?;
        loader.resolve_function(func_name)
    })
}

/// Look up a type descriptor (class) by fully-qualified name.
/// Consulted by VCall / ObjNew when the class isn't in the primary module's
/// type registry (e.g. a class from a lazily-loaded dependency).
pub fn try_lookup_type(class_name: &str) -> Option<Arc<TypeDesc>> {
    STATE.with(|s| {
        let state = s.borrow();
        state.as_ref()?.type_registry.get(class_name).cloned()
    })
}

// ── State ─────────────────────────────────────────────────────────────────────

struct LazyLoader {
    libs_dir: Option<PathBuf>,
    /// Namespaces that have already been resolved (either successfully or not).
    /// Prevents repeated zpkg-search attempts for the same namespace.
    attempted: HashSet<String>,
    /// Functions loaded from lazily-resolved zpkgs, indexed by fully-qualified name.
    function_table: HashMap<String, Arc<Function>>,
    /// Type descriptors from lazily-resolved zpkgs, indexed by class name.
    type_registry: HashMap<String, Arc<TypeDesc>>,
}

impl LazyLoader {
    fn new(libs_dir: Option<PathBuf>) -> Self {
        Self {
            libs_dir,
            attempted: HashSet::new(),
            function_table: HashMap::new(),
            type_registry: HashMap::new(),
        }
    }

    fn resolve_function(&mut self, func_name: &str) -> Option<Arc<Function>> {
        // Fast path: already loaded
        if let Some(f) = self.function_table.get(func_name) {
            return Some(Arc::clone(f));
        }

        // Try loading the zpkg that provides this namespace
        let ns = namespace_prefix(func_name)?;
        if !self.attempted.insert(ns.clone()) {
            // Already attempted (and failed, or loaded but function not in it)
            return None;
        }
        self.load_namespace(&ns).ok()?;
        self.function_table.get(func_name).map(Arc::clone)
    }

    fn load_namespace(&mut self, ns: &str) -> Result<()> {
        let dir = match &self.libs_dir {
            Some(d) => d.clone(),
            None    => return Ok(()),
        };
        // Resolve namespace → zpkg file
        let zpkg_path = match super::loader::resolve_namespace(ns, &[], &[dir])? {
            Some(p) => p,
            None    => return Ok(()),
        };
        let path_str = zpkg_path.to_string_lossy().into_owned();
        let artifact = load_artifact(&path_str)?;

        // Merge functions + type registry into lazy state
        for fn_ in artifact.module.functions {
            self.function_table.insert(fn_.name.clone(), Arc::new(fn_));
        }
        for (name, desc) in artifact.module.type_registry {
            self.type_registry.insert(name, desc);
        }
        tracing::debug!("lazy-loaded dependency `{ns}` from {path_str}");
        Ok(())
    }
}

/// Extract the namespace prefix from a fully-qualified function name.
/// E.g. "Std.IO.Console.WriteLine" → Some("Std.IO")
///      "Std.Assert.Equal"         → Some("Std")
///      "main"                     → None (no namespace)
fn namespace_prefix(func_name: &str) -> Option<String> {
    // A qualified function name has the form: <ns>.<Class>.<method>
    //                                         or <ns>.<func>
    // We attempt the longest prefix that starts with "Std." or contains ".".
    // Strategy: strip the last two segments (Class.method), keep the rest.
    let dots: Vec<usize> = func_name.match_indices('.').map(|(i, _)| i).collect();
    if dots.len() < 2 {
        // "Class.method" — no explicit namespace. Use first segment as namespace candidate.
        return dots.first().map(|&i| func_name[..i].to_string());
    }
    // Strip last two segments: "A.B.C.D" → "A.B"
    Some(func_name[..dots[dots.len() - 2]].to_string())
}

#[cfg(test)]
mod lazy_loader_tests;
