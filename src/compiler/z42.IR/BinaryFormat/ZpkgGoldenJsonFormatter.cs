using System.Text;
using System.Text.Json;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Stable JSON dump of a parsed `.zpkg` file for byte-level golden tests
/// (freeze-zpkg-v0, 2026-05-14).
///
/// Twin of <see cref="ZbcGoldenJsonFormatter"/>. Reads zpkg header + section
/// directory directly (no full ZpkgFile reconstruction), emits canonical
/// fields that don't depend on encoder layout choices.
/// </summary>
public static class ZpkgGoldenJsonFormatter
{
    /// Format zpkg bytes as canonical JSON.
    public static string Format(byte[] zpkgBytes)
    {
        var header = ParseHeader(zpkgBytes);
        var sections = ParseSectionTags(zpkgBytes, header.SecCount);
        var meta = ParseMetaSection(zpkgBytes, header.SecCount);

        var doc = new Dictionary<string, object?>
        {
            ["header"] = new Dictionary<string, object?>
            {
                ["major"]         = header.Major,
                ["minor"]         = header.Minor,
                ["flags"]         = FormatFlags(header.Flags),
                ["section_count"] = header.SecCount,
            },
            ["sections"]      = sections,
            ["package"]       = meta,
            ["is_packed"]     = (header.Flags & 0x01) != 0,
            ["is_exe"]        = (header.Flags & 0x02) != 0,
            ["is_sym_only"]   = (header.Flags & 0x04) != 0,
            ["has_blid"]      = sections.Contains("BLID"),
            ["has_mdbg"]      = sections.Contains("MDBG"),
            ["has_tsig"]      = sections.Contains("TSIG"),
            ["has_impl"]      = sections.Contains("IMPL"),
            ["has_sigs"]      = sections.Contains("SIGS"),
            ["has_mods"]      = sections.Contains("MODS"),
            ["has_file"]      = sections.Contains("FILE"),
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    // ── Header ─────────────────────────────────────────────────────────────

    private readonly record struct Header(ushort Major, ushort Minor, ushort Flags, ushort SecCount);

    private static Header ParseHeader(byte[] data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("zpkg shorter than 16 bytes");
        return new Header(
            Major:    BitConverter.ToUInt16(data, 4),
            Minor:    BitConverter.ToUInt16(data, 6),
            Flags:    BitConverter.ToUInt16(data, 8),
            SecCount: BitConverter.ToUInt16(data, 10));
    }

    private static List<string> ParseSectionTags(byte[] data, ushort secCount)
    {
        var tags = new List<string>(secCount);
        int pos = 16;
        for (int i = 0; i < secCount && pos + 12 <= data.Length; i++, pos += 12)
            tags.Add(Encoding.ASCII.GetString(data, pos, 4));
        return tags;
    }

    private static List<string> FormatFlags(ushort flags)
    {
        var result = new List<string>();
        if ((flags & 0x01) != 0) result.Add("Packed");
        if ((flags & 0x02) != 0) result.Add("Exe");
        if ((flags & 0x04) != 0) result.Add("SymOnly");
        return result;
    }

    // ── META section (parse inline UTF-8 name / version / entry) ───────────

    private static Dictionary<string, object?> ParseMetaSection(byte[] data, ushort secCount)
    {
        // Find META section via directory walk
        int pos = 16;
        for (int i = 0; i < secCount && pos + 12 <= data.Length; i++, pos += 12)
        {
            string tag = Encoding.ASCII.GetString(data, pos, 4);
            if (tag != "META") continue;
            int offset = (int)BitConverter.ToUInt32(data, pos + 4);
            int size   = (int)BitConverter.ToUInt32(data, pos + 8);
            return DecodeMetaPayload(data, offset, size);
        }
        return new Dictionary<string, object?> { ["name"] = null, ["version"] = null, ["entry"] = null };
    }

    private static Dictionary<string, object?> DecodeMetaPayload(byte[] data, int offset, int size)
    {
        int p = offset;
        int end = offset + size;
        string name    = ReadInlineString(data, ref p, end);
        string version = ReadInlineString(data, ref p, end);
        string entry   = ReadInlineString(data, ref p, end);
        return new Dictionary<string, object?>
        {
            ["name"]    = name,
            ["version"] = version,
            ["entry"]   = string.IsNullOrEmpty(entry) ? null : entry,
        };
    }

    private static string ReadInlineString(byte[] data, ref int p, int end)
    {
        if (p + 2 > end) return string.Empty;
        ushort len = BitConverter.ToUInt16(data, p);
        p += 2;
        if (p + len > end) return string.Empty;
        string s = Encoding.UTF8.GetString(data, p, len);
        p += len;
        return s;
    }
}
