using System.Text.Json.Serialization;

namespace Z42.IR;

// ── .zbc — single-file bytecode ───────────────────────────────────────────────

/// Compiled output for a single .z42 source file.
/// Phase 1: serialised as JSON.  Phase 2: binary ZBC_MAGIC + sections.
/// Matches Rust <c>package::ZbcFile</c>.
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

// ── .zmod — module manifest ────────────────────────────────────────────────────

/// Per-file entry inside a .zmod manifest.
public sealed record ZmodFileEntry(
    [property: JsonPropertyName("source")]      string      Source,
    [property: JsonPropertyName("bytecode")]    string      Bytecode,
    [property: JsonPropertyName("source_hash")] string      SourceHash,
    [property: JsonPropertyName("exports")]     List<string> Exports
);

/// External dependency declared in a .zmod.
public sealed record ZmodDep(
    [property: JsonPropertyName("name")]    string  Name,
    [property: JsonPropertyName("path")]    string  Path,
    [property: JsonPropertyName("version")] string? Version = null
);

/// Project kind.
[JsonConverter(typeof(JsonStringEnumConverter<ZmodKind>))]
public enum ZmodKind { Lib, Exe }

/// The .zmod manifest — project-level index of .zbc files and dependencies.
/// Always JSON; VCS-friendly.
/// Matches Rust <c>package::ZmodManifest</c>.
public sealed record ZmodManifest(
    [property: JsonPropertyName("zmod_version")]  int[]             ZmodVersion,
    [property: JsonPropertyName("name")]          string            Name,
    [property: JsonPropertyName("version")]       string            Version,
    [property: JsonPropertyName("kind")]          ZmodKind          Kind,
    [property: JsonPropertyName("files")]         List<ZmodFileEntry> Files,
    [property: JsonPropertyName("dependencies")]  List<ZmodDep>     Dependencies,
    [property: JsonPropertyName("entry")]         string?           Entry = null
)
{
    public static readonly int[] CurrentVersion = [0, 1];
}

// ── .zlib — assembly / library bundle ─────────────────────────────────────────

/// Exported symbol inside a .zlib.
public sealed record ZlibExport(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("kind")]   string Kind
);

/// External dependency declared in a .zlib.
public sealed record ZlibDep(
    [property: JsonPropertyName("name")]    string Name,
    [property: JsonPropertyName("version")] string Version
);

/// A .zlib assembly — bundles all .zbc files of a project into one distributable.
/// Phase 1: JSON with inlined modules.
/// Phase 2: binary ZLB_MAGIC + MANIFEST + ZBC[n] sections (see docs/design/compilation.md).
/// Matches Rust <c>package::ZlibFile</c>.
public sealed record ZlibFile(
    [property: JsonPropertyName("zlib_version")]  int[]            ZlibVersion,
    [property: JsonPropertyName("name")]          string           Name,
    [property: JsonPropertyName("version")]       string           Version,
    [property: JsonPropertyName("kind")]          ZmodKind         Kind,
    [property: JsonPropertyName("exports")]       List<ZlibExport> Exports,
    [property: JsonPropertyName("dependencies")]  List<ZlibDep>    Dependencies,
    [property: JsonPropertyName("modules")]       List<ZbcFile>    Modules,
    [property: JsonPropertyName("entry")]         string?          Entry = null
)
{
    public static readonly int[] CurrentVersion = [0, 1];
}
