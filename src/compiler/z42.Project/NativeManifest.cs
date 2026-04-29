using System.Text.Json;
using System.Text.Json.Serialization;
using Z42.Core.Diagnostics;

namespace Z42.Project;

// Spec C11a — manifest reader for `.z42abi` JSON.
// Schema source-of-truth: docs/design/manifest-schema.json (Draft 2020-12).
//
// C11a only does light validation:
//   • file exists / readable
//   • JSON is well-formed
//   • abi_version == ExpectedAbiVersion
//   • required top-level fields (module, library_name, types) are non-empty
// Deep schema validation (per-field types, enum membership) is the producing
// build infra's job; this reader is intentionally permissive about extra
// fields so manifests stay forward-compatible.

/// Static helper that loads a `.z42abi` manifest into <see cref="ManifestData"/>.
public static class NativeManifest
{
    /// The only manifest schema version this compiler accepts.
    public const int ExpectedAbiVersion = 1;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling          = JsonCommentHandling.Skip,
        AllowTrailingCommas          = true,
    };

    /// Read and validate a `.z42abi` manifest at <paramref name="path"/>.
    /// Throws <see cref="NativeManifestException"/> with code
    /// <see cref="DiagnosticCodes.ManifestParseError"/> on any failure.
    public static ManifestData Read(string path)
    {
        if (!File.Exists(path))
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"native manifest not found: {path}",
                path);

        string text;
        try   { text = File.ReadAllText(path); }
        catch (IOException ex)
        {
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"failed to read manifest `{path}`: {ex.Message}",
                path);
        }

        ManifestData? data;
        try
        {
            data = JsonSerializer.Deserialize<ManifestData>(text, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"manifest `{path}` is not valid JSON: {ex.Message}",
                path);
        }

        if (data is null)
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"manifest `{path}` deserialized to null",
                path);

        Validate(data, path);
        return data;
    }

    private static void Validate(ManifestData data, string path)
    {
        if (data.AbiVersion != ExpectedAbiVersion)
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"manifest `{path}`: abi_version {ExpectedAbiVersion} expected, got {data.AbiVersion}",
                path);

        if (string.IsNullOrEmpty(data.Module))
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"manifest `{path}`: missing required field `module`",
                path);

        if (string.IsNullOrEmpty(data.LibraryName))
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"manifest `{path}`: missing required field `library_name`",
                path);

        if (data.Types is null)
            throw new NativeManifestException(
                DiagnosticCodes.ManifestParseError,
                $"manifest `{path}`: missing required field `types`",
                path);
    }
}

/// In-memory representation of a `.z42abi` manifest.
public sealed class ManifestData
{
    [JsonPropertyName("abi_version")]
    public int AbiVersion { get; init; }

    [JsonPropertyName("module")]
    public string Module { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("library_name")]
    public string LibraryName { get; init; } = "";

    [JsonPropertyName("types")]
    public List<TypeEntry> Types { get; init; } = new();
}

public sealed class TypeEntry
{
    [JsonPropertyName("name")]    public string Name  { get; init; } = "";
    [JsonPropertyName("size")]    public long   Size  { get; init; }
    [JsonPropertyName("align")]   public long   Align { get; init; }

    [JsonPropertyName("flags")]
    public List<string> Flags { get; init; } = new();

    [JsonPropertyName("fields")]
    public List<FieldEntry> Fields { get; init; } = new();

    [JsonPropertyName("methods")]
    public List<MethodEntry> Methods { get; init; } = new();

    [JsonPropertyName("trait_impls")]
    public List<TraitImplEntry> TraitImpls { get; init; } = new();
}

public sealed class FieldEntry
{
    [JsonPropertyName("name")]      public string Name     { get; init; } = "";
    [JsonPropertyName("type")]      public string Type     { get; init; } = "";
    [JsonPropertyName("offset")]    public long   Offset   { get; init; }
    [JsonPropertyName("readonly")]  public bool   ReadOnly { get; init; }
    [JsonPropertyName("internal")]  public bool   Internal { get; init; }
}

public sealed class MethodEntry
{
    [JsonPropertyName("name")]    public string Name   { get; init; } = "";
    [JsonPropertyName("kind")]    public string Kind   { get; init; } = "method"; // ctor | method | static
    [JsonPropertyName("symbol")]  public string Symbol { get; init; } = "";

    [JsonPropertyName("params")]
    public List<ParamEntry> Params { get; init; } = new();

    [JsonPropertyName("ret")]     public string Ret    { get; init; } = "void";
}

public sealed class ParamEntry
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
}

public sealed class TraitImplEntry
{
    [JsonPropertyName("trait")]   public string Trait { get; init; } = "";

    [JsonPropertyName("methods")]
    public List<TraitImplMethod> Methods { get; init; } = new();
}

public sealed class TraitImplMethod
{
    [JsonPropertyName("name")]   public string Name   { get; init; } = "";
    [JsonPropertyName("symbol")] public string Symbol { get; init; } = "";
}
