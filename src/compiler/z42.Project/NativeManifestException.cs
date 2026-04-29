using Z42.Core.Diagnostics;

namespace Z42.Project;

/// <summary>
/// Thrown by <see cref="NativeManifest.Read"/> when a `.z42abi` manifest cannot
/// be parsed (file not found, malformed JSON, ABI version mismatch, missing
/// required field). Always carries diagnostic code <c>E0909</c>
/// (<see cref="DiagnosticCodes.ManifestParseError"/>) so the driver can route
/// it through the same error-rendering pipeline as compiler diagnostics.
///
/// Distinct from the build-manifest <see cref="ManifestException"/> in
/// <c>ProjectManifest.cs</c>, which covers WS0xx workspace errors.
/// </summary>
public sealed class NativeManifestException : Exception
{
    public string Code { get; }
    public string Path { get; }

    public NativeManifestException(string code, string message, string path)
        : base(message)
    {
        Code = code;
        Path = path;
    }
}
