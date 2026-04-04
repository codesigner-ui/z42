using System.Text.Json.Serialization;

namespace Z42.IR;

// ── .zbc — single-file bytecode ───────────────────────────────────────────────

/// Compiled output for a single .z42 source file.
/// Phase 1: serialised as JSON.  Phase 2: binary ZBC_MAGIC + sections.
/// Matches Rust <c>metadata::ZbcFile</c>.
public sealed record ZbcFile(
    [property: JsonPropertyName("zbc_version")]  int[]       ZbcVersion,
    [property: JsonPropertyName("source_file")]  string      SourceFile,
    [property: JsonPropertyName("source_hash")]  string      SourceHash,
    [property: JsonPropertyName("namespace")]    string      Namespace,
    [property: JsonPropertyName("exports")]      List<string> Exports,
    [property: JsonPropertyName("imports")]      List<string> Imports,
    [property: JsonPropertyName("module")]       IrModule    Module
)
{
    public static readonly int[] CurrentVersion = [0, 1];
}

// ── .zpkg — unified project package (indexed or packed) ───────────────────────

/// Package kind: executable or library.
/// Serialised as lowercase ("exe" / "lib") via JsonStringEnumConverter(CamelCase).
public enum ZpkgKind { Exe, Lib }

/// Package storage mode.
/// Serialised as lowercase ("indexed" / "packed") via JsonStringEnumConverter(CamelCase).
public enum ZpkgMode { Indexed, Packed }

/// Per-file entry in a .zpkg with mode=indexed (references .zbc on disk).
public sealed record ZpkgFileEntry(
    [property: JsonPropertyName("source")]      string       Source,
    [property: JsonPropertyName("bytecode")]    string       Bytecode,
    [property: JsonPropertyName("source_hash")] string       SourceHash,
    [property: JsonPropertyName("exports")]     List<string> Exports
);

/// Exported symbol entry in a .zpkg.
public sealed record ZpkgExport(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("kind")]   string Kind
);

/// External dependency declared in a .zpkg.
public sealed record ZpkgDep(
    [property: JsonPropertyName("name")]    string  Name,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("path")]    string? Path    = null
);

/// A .zpkg package — unified format for both indexed (incremental dev) and packed (distributable) modes.
///
/// mode=indexed: files[] lists .zbc paths relative to the .zpkg; modules=[]
/// mode=packed:  modules[] inlines all ZbcFiles;                  files=[]
///
/// kind=exe has entry; kind=lib has entry=null.
/// Matches Rust <c>metadata::ZpkgFile</c>.
public sealed record ZpkgFile(
    [property: JsonPropertyName("name")]          string            Name,
    [property: JsonPropertyName("version")]       string            Version,
    [property: JsonPropertyName("kind")]          ZpkgKind          Kind,
    [property: JsonPropertyName("mode")]          ZpkgMode          Mode,
    [property: JsonPropertyName("exports")]       List<ZpkgExport>  Exports,
    [property: JsonPropertyName("dependencies")]  List<ZpkgDep>     Dependencies,
    [property: JsonPropertyName("files")]         List<ZpkgFileEntry> Files,
    [property: JsonPropertyName("modules")]       List<ZbcFile>     Modules,
    [property: JsonPropertyName("entry")]         string?           Entry = null
);
