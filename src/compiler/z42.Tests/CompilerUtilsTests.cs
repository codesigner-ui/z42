using FluentAssertions;
using Xunit;
using Z42.Pipeline;

namespace Z42.Tests;

public class CompilerUtilsTests
{
    [Fact]
    public void Sha256Hex_CrlfAndLf_ProduceSameHash()
    {
        // Regression: Windows checkouts with autocrlf=true silently introduced
        // CRLF, shifting SourceHash off the LF-baseline baked into committed
        // .zpkg fixtures (FormatGoldenTests.ByteEqual). Hashing must be EOL-
        // agnostic so cross-platform CI stays deterministic.
        var lf   = "module foo;\nfn main() { }\n";
        var crlf = "module foo;\r\nfn main() { }\r\n";

        CompilerUtils.Sha256Hex(crlf).Should().Be(CompilerUtils.Sha256Hex(lf));
    }

    [Fact]
    public void Sha256Hex_BareCr_NotNormalized()
    {
        // We only canonicalize the CRLF pair (the Windows checkout artifact).
        // A bare CR is legitimate content drift that should still alter the hash.
        var lf = "a\nb";
        var cr = "a\rb";

        CompilerUtils.Sha256Hex(cr).Should().NotBe(CompilerUtils.Sha256Hex(lf));
    }

    [Fact]
    public void Sha256Hex_Format_IsSha256ColonLowerHex()
    {
        var hash = CompilerUtils.Sha256Hex("hello");
        hash.Should().StartWith("sha256:");
        hash[7..].Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
