using System.Text;
using System.Text.Json;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Stable JSON dump of an <see cref="IrModule"/> for byte-level golden tests
/// (freeze-zbc-v1, 2026-05-14).
///
/// Excludes any field whose value depends on encoder layout choices that don't
/// affect program semantics: absolute byte offsets, string pool internal ordering,
/// build_id (content-addressed but unstable across opcode permutations).
///
/// Output is canonical (sorted-key) JSON so that <c>git diff</c> on regenerated
/// fixtures shows only meaningful changes.
/// </summary>
public static class ZbcGoldenJsonFormatter
{
    /// Format an already-parsed <see cref="IrModule"/> + the source zbc header
    /// info (read separately because IrModule discards major/minor/flags).
    public static string Format(byte[] zbcBytes, IrModule module)
    {
        var header = ParseHeader(zbcBytes);
        var sections = ParseSectionTags(zbcBytes, header.SecCount);

        var doc = new Dictionary<string, object?>
        {
            ["header"] = new Dictionary<string, object?>
            {
                ["major"]        = header.Major,
                ["minor"]        = header.Minor,
                ["flags"]        = FormatFlags(header.Flags),
                ["section_count"] = header.SecCount,
            },
            ["sections"]               = sections,
            ["module"]                 = module.Name,
            ["string_pool_size"]       = module.StringPool.Count,
            ["classes"]                = module.Classes.Select(FormatClass).ToList(),
            ["functions"]              = module.Functions.Select(FormatFunc).ToList(),
            ["has_test_index"]         = module.TestIndex is { Count: > 0 },
            ["test_index_size"]        = module.TestIndex?.Count ?? 0,
            ["func_ref_cache_slots"]   = module.FuncRefCacheSlotCount,
            ["has_build_id"]           = module.BuildId is not null,
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
            throw new InvalidDataException("zbc shorter than 16 bytes");
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
        if ((flags & 0x01) != 0) result.Add("Stripped");
        if ((flags & 0x02) != 0) result.Add("HasDebug");
        if ((flags & 0x04) != 0) result.Add("SymOnly");
        return result;
    }

    // ── Classes / Functions ────────────────────────────────────────────────

    private static Dictionary<string, object?> FormatClass(IrClassDesc c)
    {
        return new Dictionary<string, object?>
        {
            ["name"]       = c.Name,
            ["base"]       = c.BaseClass,
            ["fields"]     = c.Fields.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["type"] = f.Type,
            }).ToList(),
            ["type_params"] = c.TypeParams,
        };
    }

    private static Dictionary<string, object?> FormatFunc(IrFunction f)
    {
        return new Dictionary<string, object?>
        {
            ["name"]         = f.Name,
            ["param_count"]  = f.ParamCount,
            ["param_types"]  = f.ParamTypes,
            ["return_type"]  = f.RetType,
            ["exec_mode"]    = f.ExecMode,
            ["is_static"]    = f.IsStatic,
            ["block_count"]  = f.Blocks.Count,
            ["instr_count"]  = f.Blocks.Sum(b => b.Instructions.Count),
            ["opcodes"]      = f.Blocks
                .SelectMany(b => b.Instructions)
                .Select(i => i.GetType().Name)
                .ToList(),
        };
    }
}
