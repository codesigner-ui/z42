using System.Security.Cryptography;
using System.Text;

namespace Z42.Pipeline;

/// <summary>Shared utility methods for the compilation pipeline.</summary>
public static class CompilerUtils
{
    /// <summary>Computes SHA-256 of <paramref name="text"/> and returns <c>"sha256:&lt;hex&gt;"</c>.</summary>
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
