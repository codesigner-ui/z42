using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Z42.Core.Diagnostics;

/// <summary>
/// Catalog of <c>Z####</c> VM runtime diagnostic codes.
///
/// Source of truth: <c>docs/error-codes/Z.json</c>, shared verbatim with
/// the Rust runtime (<c>src/runtime/src/diagnostics/mod.rs</c> includes the
/// same file via <c>include_str!</c>). The JSON is embedded into z42.Core
/// at build time as <c>Z42.Core.Diagnostics.Z.json</c> resource — no runtime
/// disk dependency, no path-resolution fragility.
///
/// Both <c>z42c explain Z0905</c> and <c>z42vm --explain Z0905</c> render
/// identical content because they read the same bytes.
///
/// Registered with the central <see cref="DiagnosticCatalog"/> via the
/// module initializer below.
/// </summary>
public static class RustErrorCatalog
{
    private const string ResourceName = "Z42.Core.Diagnostics.Z.json";

    private static readonly Lazy<IReadOnlyDictionary<string, DiagnosticEntry>> _all =
        new(LoadFromEmbeddedResource);

    public static IReadOnlyDictionary<string, DiagnosticEntry> All => _all.Value;

    public static DiagnosticEntry? TryGet(string code) =>
        All.TryGetValue(code, out var e) ? e : null;

    private static IReadOnlyDictionary<string, DiagnosticEntry> LoadFromEmbeddedResource()
    {
        var asm = typeof(RustErrorCatalog).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in {asm.FullName}. " +
                $"Check z42.Core.csproj <EmbeddedResource Include=...>.");

        var doc = JsonSerializer.Deserialize<RawCatalog>(stream, JsonOpts)
            ?? throw new InvalidOperationException($"Failed to parse embedded {ResourceName}.");

        var dict = new Dictionary<string, DiagnosticEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in doc.Entries ?? new List<RawEntry>())
        {
            if (string.IsNullOrEmpty(raw.Code)) continue;
            dict[raw.Code] = new DiagnosticEntry(
                Title:       raw.Title       ?? "(no title)",
                Description: raw.Description ?? "(no description)",
                Example:     string.IsNullOrEmpty(raw.Example) ? null : raw.Example);
        }
        return dict;
    }

    /// Idempotent registration with the central catalog.
    private static bool _registered;
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        DiagnosticCatalog.RegisterExternal(TryGet, () => All);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Wire formats — match the JSON schema in docs/error-codes/Z.json.
    private sealed class RawCatalog
    {
        [JsonPropertyName("entries")]
        public List<RawEntry>? Entries { get; set; }
    }

    private sealed class RawEntry
    {
        [JsonPropertyName("code")]        public string? Code        { get; set; }
        [JsonPropertyName("title")]       public string? Title       { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("example")]     public string? Example     { get; set; }
    }
}

/// <summary>
/// Module initializer that auto-registers <see cref="RustErrorCatalog"/>
/// with the central registry as soon as Z42.Core loads.
/// </summary>
internal static class RustErrorCatalogModuleInitializer
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => RustErrorCatalog.Register();
}
