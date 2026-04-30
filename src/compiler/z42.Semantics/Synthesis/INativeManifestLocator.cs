using Z42.Core.Diagnostics;
using Z42.Core.Text;

namespace Z42.Semantics.Synthesis;

/// <summary>
/// Resolves an <c>import T from "&lt;libName&gt;";</c> declaration to the absolute
/// path of the corresponding <c>.z42abi</c> manifest. Pluggable so tests can
/// inject in-memory manifests without touching the real filesystem.
/// </summary>
public interface INativeManifestLocator
{
    /// <summary>
    /// Return the absolute path of the manifest for <paramref name="libName"/>,
    /// or throw <see cref="NativeImportException"/> with code
    /// <see cref="DiagnosticCodes.NativeImportSynthesisFailure"/> if no
    /// candidate path resolves to an existing file. <paramref name="sourceDir"/>
    /// (when non-null) is searched first.
    /// </summary>
    string Locate(string libName, string? sourceDir, Span importSpan);
}

/// <summary>
/// Default locator: tries <c>&lt;sourceDir&gt;/&lt;libName&gt;.z42abi</c> first,
/// then each entry of <c>Z42_NATIVE_LIBS_PATH</c> (colon-separated).
/// </summary>
public sealed class DefaultNativeManifestLocator : INativeManifestLocator
{
    private const string EnvVarName = "Z42_NATIVE_LIBS_PATH";

    public string Locate(string libName, string? sourceDir, Span importSpan)
    {
        var fileName = libName + ".z42abi";
        var tried    = new List<string>();

        if (!string.IsNullOrEmpty(sourceDir))
        {
            var candidate = Path.Combine(sourceDir, fileName);
            tried.Add(candidate);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var envPath = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrEmpty(envPath))
        {
            foreach (var dir in envPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, fileName);
                tried.Add(candidate);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        throw new NativeImportException(
            DiagnosticCodes.NativeImportSynthesisFailure,
            $"native manifest for `{libName}` not found; tried: " + string.Join(", ", tried),
            importSpan);
    }
}

/// <summary>
/// Test-only locator: maps <c>libName</c> to a JSON string supplied at
/// construction time. The JSON is written to a temp file on first lookup so
/// that <c>NativeManifest.Read</c> exercises its real I/O + parse path.
/// </summary>
public sealed class InMemoryManifestLocator : INativeManifestLocator
{
    private readonly Dictionary<string, string> _byLib;
    private readonly Dictionary<string, string> _resolvedPaths = new();

    public InMemoryManifestLocator(Dictionary<string, string> manifestJsonByLib)
    {
        _byLib = manifestJsonByLib;
    }

    public string Locate(string libName, string? sourceDir, Span importSpan)
    {
        if (_resolvedPaths.TryGetValue(libName, out var cached))
            return cached;

        if (!_byLib.TryGetValue(libName, out var json))
            throw new NativeImportException(
                DiagnosticCodes.NativeImportSynthesisFailure,
                $"InMemoryManifestLocator has no manifest for `{libName}`",
                importSpan);

        var path = Path.Combine(Path.GetTempPath(),
            $"z42abi-test-{libName}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _resolvedPaths[libName] = path;
        return path;
    }
}
