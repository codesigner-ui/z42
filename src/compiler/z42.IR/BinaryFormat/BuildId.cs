using Blake3;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Build identifier for split-debug-symbols (zbc 1.2+).
///
/// A build_id is BLAKE3-128 (first 16 bytes of BLAKE3-256) of the entire
/// main `.zbc` byte stream, with the BLID section's 16-byte payload zeroed
/// before hashing. The same build_id is written into both the main file's
/// BLID section and its sidecar `.zsym`, so the loader can verify pairing.
///
/// "Integrity-zeroed" hashing: the BLID section is required to be the
/// last section of the main file (writer enforces this), so the layout is
/// deterministic and the zeroing is done in-place on a copy of the buffer.
/// </summary>
public static class BuildId
{
    public const int Size = 16;

    /// <summary>
    /// Computes BLAKE3-128 over <paramref name="zbcBytes"/>, treating the
    /// last 16 bytes as zero. Returns a 16-byte build_id.
    /// </summary>
    /// <remarks>
    /// Caller is responsible for ensuring the BLID section's 16 bytes are
    /// at the tail of <paramref name="zbcBytes"/>. The function does not
    /// inspect section directory; it just zeroes the trailing 16 bytes for
    /// the hash input.
    /// </remarks>
    public static byte[] Compute(ReadOnlySpan<byte> zbcBytes)
    {
        if (zbcBytes.Length < Size)
            throw new ArgumentException($"zbc bytes must be at least {Size} bytes", nameof(zbcBytes));

        // Hash everything but the trailing 16 bytes (the BLID payload),
        // then add 16 zero bytes. Equivalent to "copy + zero tail + hash"
        // without the allocation.
        var hasher = Hasher.New();
        hasher.UpdateWithJoin(zbcBytes[..^Size]);
        Span<byte> zeros = stackalloc byte[Size];
        hasher.Update(zeros);
        Span<byte> full = stackalloc byte[32];
        hasher.Finalize(full);
        return full[..Size].ToArray();
    }

    /// <summary>
    /// Formats the first 4 bytes of a build_id as 8 lowercase hex chars
    /// (matching the runtime's `[build:abcd1234]` trace suffix).
    /// </summary>
    public static string ShortHex(ReadOnlySpan<byte> buildId)
    {
        if (buildId.Length < 4)
            throw new ArgumentException("build_id must be at least 4 bytes", nameof(buildId));
        return $"{buildId[0]:x2}{buildId[1]:x2}{buildId[2]:x2}{buildId[3]:x2}";
    }
}
