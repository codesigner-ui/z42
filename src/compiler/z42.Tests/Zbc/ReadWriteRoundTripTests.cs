using FluentAssertions;
using Xunit;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Tests.Zbc;

/// <summary>
/// Read → Write byte-parity CI防线 for the zbc wire format.
///
/// Loads each fixture's checked-in <c>source.zbc</c>, decodes it via
/// <see cref="ZbcReader"/>, re-encodes via <see cref="ZbcWriter"/>, and
/// asserts byte-equality.
///
/// Originally drafted as part of freeze-zbc-v1 (2026-05-14) but DROPPED at the
/// time because <c>retType</c> / field type went through lossy 1-byte
/// <c>TypeTag</c> encoding (`"int"` → `I32` → canonical `"i32"`). Re-enabled
/// 2026-05-27 by <c>align-zbc-reader-writer-asymmetry</c> (zbc 1.7):
/// SIGS / TYPE now carry a u32 type_str_idx after the type tag, so reader
/// recovers the original string, writer emits identical bytes.
///
/// Fails ⇒ either reader-writer 对称性退化（一定不要回到 Option C），或者
/// 新加的字段没在 reader-writer 对称对齐。两种情况都该停下来 root-cause 修。
/// </summary>
public class ReadWriteRoundTripTests
{
    private static readonly string FixtureRoot = FindFixtureRoot();

    public static IEnumerable<object[]> AllFixtures() =>
        Directory.Exists(FixtureRoot)
            ? Directory.EnumerateDirectories(FixtureRoot)
                .Where(d => File.Exists(Path.Combine(d, "source.zbc")))
                .Select(d => new object[] { Path.GetFileName(d)! })
                .OrderBy(o => (string)o[0])
            : [];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void ReadThenWriteIsByteIdentical(string fixture)
    {
        string zbcPath = Path.Combine(FixtureRoot, fixture, "source.zbc");
        File.Exists(zbcPath).Should().BeTrue(
            $"fixture `{fixture}` missing source.zbc — run src/tests/zbc-format/generate-fixtures.sh");

        byte[] original = File.ReadAllBytes(zbcPath);
        IrModule decoded = ZbcReader.Read(original);
        byte[] roundTrip = ZbcWriter.Write(decoded);

        roundTrip.Should().Equal(original,
            because: $"fixture `{fixture}` Read→Write byte parity broken. " +
                     $"Either a reader/writer pair lost symmetry (e.g. new lossy field), " +
                     $"or string pool ordering differs. Investigate root cause — do NOT " +
                     $"\"fix\" by re-regenerating the fixture, that hides the bug.");
    }

    private static string FindFixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "tests", "zbc-format");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return string.Empty;
    }
}
