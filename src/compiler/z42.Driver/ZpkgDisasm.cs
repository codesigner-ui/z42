using System.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;

namespace Z42.Driver;

/// <summary>
/// Renders a <c>.zpkg</c> file as a human-readable text dump for the
/// <c>z42c disasm</c> subcommand.
///
/// Output layout:
///   ; ==========================
///   ; zpkg: name v0.1.0 (lib, packed)
///   ; ==========================
///   ; entry:        <entry|none>
///   ; flags:        Packed [Exe] [SymOnly]
///   ; build_id:     <hex|absent>
///   ;
///   ; namespaces:   ns1, ns2, ...
///   ; exports:
///   ;   - Symbol           (kind=func)
///   ; dependencies:
///   ;   - dep_name v0.1.0  → ns1, ns2
///   ; tsig modules: N        ; or "(none)"
///   ; impl entries: N
///   ;
///   ; ==========================
///   ; module: ns
///   ; ==========================
///   &lt;zasm body&gt;
///
/// freeze-zpkg-v0 follow-up (2026-05-14).
/// </summary>
public static class ZpkgDisasm
{
    public static string Format(byte[] raw)
    {
        var sb   = new StringBuilder();
        var meta = ZpkgReader.ReadMeta(raw);

        // Read raw flag byte for accurate SymOnly detection (BuildId presence
        // doesn't imply SymOnly — strip-mode main zpkg also has BLID).
        ushort rawFlags = BitConverter.ToUInt16(raw, 8);
        bool isSymOnly  = (rawFlags & 0x04) != 0;

        // SymOnly sidecars have no MODS section — skip module dump for them.
        IReadOnlyList<(IrModule Module, string Namespace)> modules = isSymOnly
            ? []
            : ZpkgReader.ReadModules(raw);

        // Try TSIG/IMPL — may not be present (also absent in sidecars)
        int tsigCount = 0;
        int implCount = 0;
        if (!isSymOnly)
        {
            try
            {
                var exported = ZpkgReader.ReadTsig(raw);
                tsigCount = exported.Count;
                implCount = exported.Sum(m => m.Impls?.Count ?? 0);
            }
            catch
            {
                // TSIG absent — leave counts at 0
            }
        }

        var flags = new List<string> { meta.Mode == ZpkgMode.Packed ? "Packed" : "Indexed" };
        if (meta.Kind == ZpkgKind.Exe) flags.Add("Exe");
        if (isSymOnly)                 flags.Add("SymOnly");

        WriteHeader(sb, $"zpkg: {meta.Name} v{meta.Version} ({meta.Kind.ToString().ToLowerInvariant()}, {meta.Mode.ToString().ToLowerInvariant()})");
        sb.AppendLine($"; entry:        {meta.Entry ?? "(none)"}");
        sb.AppendLine($"; flags:        {string.Join(" | ", flags)}");
        sb.AppendLine($"; build_id:     {(meta.BuildId is not null ? Convert.ToHexString(meta.BuildId).ToLowerInvariant() : "(absent)")}");
        sb.AppendLine(";");

        sb.AppendLine($"; namespaces:   {(meta.Namespaces.Count == 0 ? "(none)" : string.Join(", ", meta.Namespaces))}");

        sb.AppendLine($"; exports ({meta.Exports?.Count ?? 0}):");
        if (meta.Exports is { Count: > 0 })
            foreach (var e in meta.Exports)
                sb.AppendLine($";   - {e.Symbol}  (kind={e.Kind})");

        sb.AppendLine($"; dependencies ({meta.Dependencies.Count}):");
        if (meta.Dependencies.Count > 0)
            foreach (var d in meta.Dependencies)
                sb.AppendLine($";   - {d.File} → {string.Join(", ", d.Namespaces)}");

        sb.AppendLine($"; tsig modules: {tsigCount}");
        sb.AppendLine($"; impl entries: {implCount}");
        sb.AppendLine();

        foreach (var (mod, ns) in modules)
        {
            WriteHeader(sb, $"module: {ns}");
            sb.AppendLine(ZasmWriter.Write(mod));
        }

        return sb.ToString();
    }

    private static void WriteHeader(StringBuilder sb, string title)
    {
        sb.AppendLine("; ===========================================================================");
        sb.AppendLine($"; {title}");
        sb.AppendLine("; ===========================================================================");
    }
}
