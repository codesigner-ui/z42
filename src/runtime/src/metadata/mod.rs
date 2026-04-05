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
pub mod formats;
pub mod loader;
pub mod merge;

// Re-exports: runtime value types
pub use types::{ExecMode, ObjectData, Value};

// Re-exports: bytecode IR structures
pub use bytecode::{BasicBlock, ClassDesc, ExceptionEntry, FieldDesc, Function, Instruction, Module, Terminator};

// Re-exports: package format types and artifact loading
pub use formats::{ZbcFile, ZpkgFile};
pub use loader::{load_artifact, LoadedArtifact};
pub use merge::merge_modules;
