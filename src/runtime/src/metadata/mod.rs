/// Compiler output format parsing and runtime metadata definitions.
///
/// Submodules:
///   `types`    — runtime value types (Value, ExecMode, ObjectData)
///   `bytecode` — IR data structures (Module, Function, Instruction, Terminator)
///   `formats`  — data structures for .zbc / .zpkg (mirrors C# PackageTypes.cs)
///   `merge`    — multi-module merge algorithm (string pool remap + function concat)
///   `loader`   — format-dispatch entry point: `load_artifact(path)`

pub mod types;
pub mod bytecode;
mod bytecode_serde;
pub mod project;
pub mod formats;
pub mod zbc_reader;
pub mod loader;
pub mod lazy_loader;
pub mod merge;
pub mod well_known_names;

#[cfg(test)]
#[path = "constraint_tests.rs"]
mod constraint_tests;

// Re-exports: runtime value types
pub use types::{ExecMode, FieldSlot, NativeData, PinSourceKind, ScriptObject, TypeDesc, Value};
#[allow(deprecated)]
pub use types::ObjectData;

// Re-exports: bytecode IR structures
pub use bytecode::{BasicBlock, ClassDesc, ExceptionEntry, FieldDesc, Function, Instruction, Module, Terminator};

// Re-exports: package format types and artifact loading
pub use formats::{ZbcFile, ZpkgFile};
pub use loader::{load_artifact, resolve_namespace, resolve_dependency, extract_import_namespaces, LoadedArtifact};
pub use merge::merge_modules;

// Re-exports: lazy loader (state owned by VmContext, see crate::vm_context)
pub use lazy_loader::{LazyLoader, ZpkgCandidate};
