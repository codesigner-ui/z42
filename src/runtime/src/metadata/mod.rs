/// Compiler output format parsing and runtime metadata definitions.
///
/// Submodules:
///   `formats` — data structures for .zbc / .zmod / .zbin (mirrors C# PackageTypes.cs)
///   `merge`   — multi-module merge algorithm (string pool remap + function concat)
///   `loader`  — format-dispatch entry point: `load_artifact(path)`

pub mod formats;
pub mod loader;
pub mod merge;

// Convenience re-exports for callers.
pub use formats::{ZbcFile, ZbinFile, ZmodManifest};
pub use loader::{load_artifact, LoadedArtifact};
