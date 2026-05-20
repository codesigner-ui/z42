using System.Security.Cryptography;
using System.Text;

namespace Z42.Pipeline;

/// <summary>Shared utility methods for the compilation pipeline.</summary>
public static class CompilerUtils
{
    /// <summary>Computes SHA-256 of <paramref name="text"/> and returns <c>"sha256:&lt;hex&gt;"</c>.</summary>
    /// <remarks>
    /// CRLF is canonicalized to LF before hashing so the hash is stable across
    /// Windows / Linux / macOS checkouts. Without this, a Windows checkout with
    /// <c>autocrlf=true</c> hashes raw CRLF bytes → <c>SourceHash</c> drifts
    /// from the LF-based hash baked into committed <c>.zpkg</c> fixtures, and
    /// <c>FormatGoldenTests.ByteEqual</c> fails. <c>.gitattributes</c> pins
    /// <c>*.z42</c> to <c>eol=lf</c> as the primary defense; this normalization
    /// is defense-in-depth against future <c>.gitattributes</c> drift or
    /// callers that pass already-loaded CRLF text.
    /// </remarks>
    public static string Sha256Hex(string text)
    {
        var canonical = text.Replace("\r\n", "\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
