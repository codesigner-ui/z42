using FluentAssertions;
using Xunit;
using Z42.IR.BinaryFormat;

namespace Z42.Tests.Zbc;

/// <summary>
/// Invariants that protect zbc v1 wire format from accidental drift.
/// Spec: <c>docs/spec/archive/&lt;date&gt;-freeze-zbc-v1/</c>.
///
/// These tests don't compile any z42 source — they construct zbc byte streams
/// directly (or trivial IrModules) to exercise reader policy without depending
/// on stdlib / IrGen / TypeChecker.
/// </summary>
public class FormatInvariantTests
{
    // ── Header construction helpers ────────────────────────────────────────

    private static byte[] BuildMinimalZbcHeader(ushort major, ushort minor, ushort flags = 0)
    {
        // 16-byte header + 0 sections
        var data = new byte[16];
        data[0] = (byte)'Z'; data[1] = (byte)'B'; data[2] = (byte)'C'; data[3] = 0;
        BitConverter.GetBytes(major).CopyTo(data, 4);
        BitConverter.GetBytes(minor).CopyTo(data, 6);
        BitConverter.GetBytes(flags).CopyTo(data, 8);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 10); // sec_count = 0
        return data;
    }

    // ── Single-source-of-truth: writer's VersionMajor / VersionMinor ──────

    [Fact]
    public void WriterVersionConstantsExposed()
    {
        // Sanity: the writer publishes its current version as a public const.
        // The reader and tests reference this single source.
        ZbcWriter.VersionMajor.Should().Be(1, "freeze-zbc-v1 locked major at 1");
        ZbcWriter.VersionMinor.Should().BeGreaterThan(0, "minor starts at 0 / increments per bump");
    }

    // ── Strict-pin reject: major mismatch ──────────────────────────────────

    [Fact]
    public void MajorMismatch_Rejected()
    {
        // Future major (2) — must be rejected.
        byte[] bogus = BuildMinimalZbcHeader(major: 2, minor: ZbcWriter.VersionMinor);

        Action act = () => ZbcReader.Read(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*major*not supported*");
    }

    [Fact]
    public void MajorZero_Rejected()
    {
        // pre-1.0 zbc must be rejected (Major == 0).
        byte[] bogus = BuildMinimalZbcHeader(major: 0, minor: 9);

        Action act = () => ZbcReader.Read(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*major 0*not supported*");
    }

    // ── Strict-pin reject: minor mismatch ──────────────────────────────────

    [Fact]
    public void MinorBelowWriter_Rejected()
    {
        if (ZbcWriter.VersionMinor == 0)
            return; // no "below" if writer is at 0

        byte[] bogus = BuildMinimalZbcHeader(
            major: ZbcWriter.VersionMajor,
            minor: (ushort)(ZbcWriter.VersionMinor - 1));

        Action act = () => ZbcReader.Read(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*minor*not supported*regen via*");
    }

    [Fact]
    public void MinorAboveWriter_Rejected()
    {
        byte[] bogus = BuildMinimalZbcHeader(
            major: ZbcWriter.VersionMajor,
            minor: (ushort)(ZbcWriter.VersionMinor + 1));

        Action act = () => ZbcReader.Read(bogus);
        act.Should()
            .Throw<InvalidDataException>()
            .WithMessage("*minor*not supported*regen via*");
    }

    // ── Unknown section silently skipped ───────────────────────────────────

    [Fact]
    public void UnknownSection_SilentlySkipped()
    {
        // Build a zbc with header + 1 directory entry pointing to an "ZZZZ" section
        // containing 4 dummy bytes. Reader has no handler for ZZZZ, so dict-lookup
        // simply ignores it. Must NOT throw.
        const int headerSize    = 16;
        const int dirEntrySize  = 12;
        const int sectionSize   = 4;
        int totalSize           = headerSize + dirEntrySize + sectionSize;
        var data                = new byte[totalSize];

        // Header
        data[0] = (byte)'Z'; data[1] = (byte)'B'; data[2] = (byte)'C'; data[3] = 0;
        BitConverter.GetBytes(ZbcWriter.VersionMajor).CopyTo(data, 4);
        BitConverter.GetBytes(ZbcWriter.VersionMinor).CopyTo(data, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(data, 8); // flags
        BitConverter.GetBytes((ushort)1).CopyTo(data, 10); // sec_count = 1

        // Directory entry @ offset 16: tag "ZZZZ", offset 28, size 4
        data[16] = (byte)'Z'; data[17] = (byte)'Z'; data[18] = (byte)'Z'; data[19] = (byte)'Z';
        BitConverter.GetBytes((uint)28).CopyTo(data, 20);
        BitConverter.GetBytes((uint)4).CopyTo(data, 24);

        // Unknown section payload (4 dummy bytes)
        data[28] = 0xDE; data[29] = 0xAD; data[30] = 0xBE; data[31] = 0xEF;

        // Reader must accept and produce an empty IrModule (no known sections present).
        Action act = () => ZbcReader.Read(data);
        act.Should().NotThrow("freeze-zbc-v1 Decision 3: unknown sections are silently skipped");
    }
}
