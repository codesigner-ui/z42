using FluentAssertions;
using Xunit;
using Z42.Project;

namespace Z42.Tests.Zpkg;

/// <summary>
/// Invariants that protect zpkg v0 wire format from accidental drift.
/// Spec: <c>docs/spec/archive/&lt;date&gt;-freeze-zpkg-v0/</c>.
///
/// Construct zpkg byte streams directly to exercise reader policy without
/// depending on stdlib / IrGen / TypeChecker.
/// </summary>
public class FormatInvariantTests
{
    private static byte[] BuildMinimalZpkgHeader(ushort major, ushort minor, ushort flags = 0)
    {
        // 16-byte header with 0 sections
        var data = new byte[16];
        data[0] = (byte)'Z'; data[1] = (byte)'P'; data[2] = (byte)'K'; data[3] = 0;
        BitConverter.GetBytes(major).CopyTo(data, 4);
        BitConverter.GetBytes(minor).CopyTo(data, 6);
        BitConverter.GetBytes(flags).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 10); // sec_count = 0
        return data;
    }

    [Fact]
    public void WriterVersionConstantsExposed()
    {
        ZpkgWriter.VersionMajor.Should().Be(0, "freeze-zpkg-v0 locked major at 0");
        ZpkgWriter.VersionMinor.Should().BeGreaterThan(0, "minor starts at 1 / increments per bump");
    }

    [Fact]
    public void MajorMismatch_Rejected()
    {
        byte[] bogus = BuildMinimalZpkgHeader(major: 1, minor: ZpkgWriter.VersionMinor);

        Action act = () => ZpkgReader.ReadMeta(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*major*not supported*");
    }

    [Fact]
    public void MinorBelowWriter_Rejected()
    {
        if (ZpkgWriter.VersionMinor == 0)
            return; // no "below" if writer is at 0

        byte[] bogus = BuildMinimalZpkgHeader(
            major: ZpkgWriter.VersionMajor,
            minor: (ushort)(ZpkgWriter.VersionMinor - 1));

        Action act = () => ZpkgReader.ReadMeta(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*minor*not supported*regen via*");
    }

    [Fact]
    public void MinorAboveWriter_Rejected()
    {
        byte[] bogus = BuildMinimalZpkgHeader(
            major: ZpkgWriter.VersionMajor,
            minor: (ushort)(ZpkgWriter.VersionMinor + 1));

        Action act = () => ZpkgReader.ReadMeta(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*minor*not supported*regen via*");
    }

    [Fact]
    public void UnknownSection_SilentlySkipped()
    {
        // Build a zpkg with header + 1 directory entry pointing to an "XXXX" section.
        // Reader has no handler for XXXX, so dict-lookup ignores it. Must NOT throw.
        // The zpkg has no META section so package name will be empty — but reader
        // tolerates that (META is treated as optional metadata).
        const int headerSize   = 16;
        const int dirEntrySize = 12;
        const int sectionSize  = 4;
        int totalSize          = headerSize + dirEntrySize + sectionSize;
        var data               = new byte[totalSize];

        data[0] = (byte)'Z'; data[1] = (byte)'P'; data[2] = (byte)'K'; data[3] = 0;
        BitConverter.GetBytes(ZpkgWriter.VersionMajor).CopyTo(data, 4);
        BitConverter.GetBytes(ZpkgWriter.VersionMinor).CopyTo(data, 6);
        BitConverter.GetBytes((ushort)0x01).CopyTo(data, 8);  // FlagPacked (so reader doesn't take sym-only path)
        BitConverter.GetBytes((ushort)1).CopyTo(data, 10);    // sec_count = 1

        // Directory entry @ offset 16: tag "XXXX", offset 28, size 4
        data[16] = (byte)'X'; data[17] = (byte)'X'; data[18] = (byte)'X'; data[19] = (byte)'X';
        BitConverter.GetBytes((uint)28).CopyTo(data, 20);
        BitConverter.GetBytes((uint)4).CopyTo(data, 24);

        data[28] = 0xDE; data[29] = 0xAD; data[30] = 0xBE; data[31] = 0xEF;

        // Reader must accept the file without throwing on unknown section.
        Action act = () => ZpkgReader.ReadMeta(data);
        act.Should().NotThrow("freeze-zpkg-v0 Decision 3: unknown sections are silently skipped");
    }
}
