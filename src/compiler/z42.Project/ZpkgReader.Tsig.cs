using System.Text;
using Z42.IR;

namespace Z42.Project;

/// TSIG（类型签名 — 跨 zpkg 引用编译用）+ IMPL（L3-Impl2 跨 zpkg `impl Trait
/// for Type` 声明）+ 共享方法/约束读取工具。与 ZpkgReader.Sections.cs 的基础
/// 段读取分离。
public static partial class ZpkgReader
{
    // ── TSIG section (type signatures for reference compilation) ────────────

    /// <summary>Read exported type signatures from the TSIG section.
    /// L3-Impl2: also reads IMPL section if present and attaches Impls list per module.</summary>
    public static List<ExportedModule> ReadTsig(byte[] data)
    {
        var (_, dir) = ParseHeaderAndDirectory(data);
        string[] pool = ReadStrs(data, dir);
        var modules = ReadTsigSection(data, dir, pool);
        ReadImplSection(data, dir, pool, modules);
        return modules;
    }

    private static List<ExportedModule> ReadTsigSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir, string[] pool)
    {
        if (!dir.TryGetValue("TSIG", out var e)) return [];
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        ushort modCount = r.ReadUInt16();
        var result = new List<ExportedModule>(modCount);

        for (int mi = 0; mi < modCount; mi++)
        {
            string ns = P(pool, r.ReadUInt32());

            // Classes
            ushort clsCount = r.ReadUInt16();
            var classes = new List<ExportedClassDef>(clsCount);
            for (int ci = 0; ci < clsCount; ci++)
            {
                string name    = P(pool, r.ReadUInt32());
                uint baseRaw   = r.ReadUInt32();
                string? baseCls = baseRaw == uint.MaxValue ? null : P(pool, baseRaw);
                byte flags     = r.ReadByte();
                bool isAbstract = (flags & 0x01) != 0;
                bool isSealed   = (flags & 0x02) != 0;
                bool isStatic   = (flags & 0x04) != 0;

                ushort fldCount = r.ReadUInt16();
                var fields = new List<ExportedFieldDef>(fldCount);
                for (int fi = 0; fi < fldCount; fi++)
                {
                    string fn = P(pool, r.ReadUInt32());
                    string ft = P(pool, r.ReadUInt32());
                    string fv = P(pool, r.ReadUInt32());
                    bool   fs = r.ReadByte() != 0;
                    fields.Add(new ExportedFieldDef(fn, ft, fv, fs));
                }

                ushort mthCount = r.ReadUInt16();
                var methods = new List<ExportedMethodDef>(mthCount);
                for (int mi2 = 0; mi2 < mthCount; mi2++)
                    methods.Add(ReadMethodDef(r, pool));

                ushort ifaceCount = r.ReadUInt16();
                var ifaces = new List<string>(ifaceCount);
                for (int ii = 0; ii < ifaceCount; ii++)
                    ifaces.Add(P(pool, r.ReadUInt32()));

                // L3-G4d: generic type parameters (forward-compatible: older zpkg
                // sections may end here; we guard on remaining bytes before reading).
                List<string>? typeParams = null;
                if (ms.Position < ms.Length)
                {
                    byte tpCount = r.ReadByte();
                    if (tpCount > 0)
                    {
                        typeParams = new List<string>(tpCount);
                        for (int ti = 0; ti < tpCount; ti++)
                            typeParams.Add(P(pool, r.ReadUInt32()));
                    }
                }
                // L3-G3d: where-clause constraints (forward-compatible).
                var tpConstraints = ReadTpConstraints(r, ms, pool);

                classes.Add(new ExportedClassDef(name, baseCls, isAbstract, isSealed, isStatic,
                    fields, methods, ifaces, typeParams, tpConstraints));
            }

            // Interfaces
            ushort ifcCount = r.ReadUInt16();
            var interfaces = new List<ExportedInterfaceDef>(ifcCount);
            for (int ii = 0; ii < ifcCount; ii++)
            {
                string name     = P(pool, r.ReadUInt32());
                ushort mthCount = r.ReadUInt16();
                var methods = new List<ExportedMethodDef>(mthCount);
                for (int mi2 = 0; mi2 < mthCount; mi2++)
                    methods.Add(ReadMethodDef(r, pool));
                // L3 primitive-as-struct: read interface TypeParams (may be 0).
                byte tpCount = (ms.Position < ms.Length) ? r.ReadByte() : (byte)0;
                List<string>? tps = null;
                if (tpCount > 0)
                {
                    tps = new List<string>(tpCount);
                    for (int ti = 0; ti < tpCount; ti++)
                        tps.Add(P(pool, r.ReadUInt32()));
                }
                interfaces.Add(new ExportedInterfaceDef(name, methods, tps));
            }

            // Enums
            ushort enumCount = r.ReadUInt16();
            var enums = new List<ExportedEnumDef>(enumCount);
            for (int ei = 0; ei < enumCount; ei++)
            {
                string name     = P(pool, r.ReadUInt32());
                ushort memCount = r.ReadUInt16();
                var members = new List<ExportedEnumMember>(memCount);
                for (int mi2 = 0; mi2 < memCount; mi2++)
                {
                    string mn = P(pool, r.ReadUInt32());
                    long   mv = r.ReadInt64();
                    members.Add(new ExportedEnumMember(mn, mv));
                }
                enums.Add(new ExportedEnumDef(name, members));
            }

            // Functions
            ushort fnCount = r.ReadUInt16();
            var functions = new List<ExportedFuncDef>(fnCount);
            for (int fi = 0; fi < fnCount; fi++)
            {
                string name    = P(pool, r.ReadUInt32());
                string retType = P(pool, r.ReadUInt32());
                ushort minArgs = r.ReadUInt16();
                byte paramCnt  = r.ReadByte();
                var parms = new List<ExportedParamDef>(paramCnt);
                for (int pi = 0; pi < paramCnt; pi++)
                {
                    string pn = P(pool, r.ReadUInt32());
                    string pt = P(pool, r.ReadUInt32());
                    parms.Add(new ExportedParamDef(pn, pt));
                }
                // L3-G3d: generic type params + where-clause constraints (forward-compat).
                List<string>? fnTypeParams = null;
                if (ms.Position < ms.Length)
                {
                    byte fnTpCount = r.ReadByte();
                    if (fnTpCount > 0)
                    {
                        fnTypeParams = new List<string>(fnTpCount);
                        for (int ti = 0; ti < fnTpCount; ti++)
                            fnTypeParams.Add(P(pool, r.ReadUInt32()));
                    }
                }
                var fnConstraints = ReadTpConstraints(r, ms, pool);
                functions.Add(new ExportedFuncDef(name, parms, retType, minArgs,
                    fnTypeParams, fnConstraints));
            }

            // 2026-05-02 add-generic-delegates (D1c): Delegates trailer.
            // Position-guard for forward compat with pre-D1c zpkgs.
            List<ExportedDelegateDef>? delegates = null;
            if (ms.Position < ms.Length)
            {
                ushort dgCount = r.ReadUInt16();
                if (dgCount > 0)
                {
                    delegates = new List<ExportedDelegateDef>(dgCount);
                    for (int di = 0; di < dgCount; di++)
                    {
                        string dName    = P(pool, r.ReadUInt32());
                        string dRetType = P(pool, r.ReadUInt32());
                        byte dParamCnt  = r.ReadByte();
                        var dParms = new List<ExportedParamDef>(dParamCnt);
                        for (int pi = 0; pi < dParamCnt; pi++)
                        {
                            string pn = P(pool, r.ReadUInt32());
                            string pt = P(pool, r.ReadUInt32());
                            dParms.Add(new ExportedParamDef(pn, pt));
                        }
                        byte dTpCount = r.ReadByte();
                        List<string>? dTypeParams = null;
                        if (dTpCount > 0)
                        {
                            dTypeParams = new List<string>(dTpCount);
                            for (int ti = 0; ti < dTpCount; ti++)
                                dTypeParams.Add(P(pool, r.ReadUInt32()));
                        }
                        uint cclsRaw = r.ReadUInt32();
                        string? containerClass = cclsRaw == uint.MaxValue ? null : P(pool, cclsRaw);
                        delegates.Add(new ExportedDelegateDef(
                            dName, dParms, dRetType, dTypeParams, containerClass));
                    }
                }
            }

            result.Add(new ExportedModule(
                ns, classes, interfaces, enums, functions,
                Impls: null, Delegates: delegates));
        }
        return result;
    }

    // ── IMPL section (L3-Impl2: cross-zpkg `impl Trait for Type`) ─────────────

    /// Read IMPL section and attach `Impls` list to each ExportedModule by index.
    /// IMPL section emits one record per ExportedModule in the same order as TSIG;
    /// positional matching is required because multiple modules can share a
    /// namespace (z42.core has many .z42 files all in `Std`).
    /// Older zpkg (pre-0.8) lacks IMPL section — modules left with `Impls = null`.
    private static void ReadImplSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir,
        string[] pool, List<ExportedModule> modules)
    {
        if (!dir.TryGetValue("IMPL", out var e)) return;
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        ushort modCount = r.ReadUInt16();
        for (int mi = 0; mi < modCount; mi++)
        {
            // Namespace pool idx — informational, not used for matching (positional).
            r.ReadUInt32();
            ushort implCount = r.ReadUInt16();
            var impls = new List<ExportedImplDef>(implCount);
            for (int ii = 0; ii < implCount; ii++)
            {
                string targetFq = P(pool, r.ReadUInt32());
                string traitFq  = P(pool, r.ReadUInt32());
                byte argCount   = r.ReadByte();
                var args = new List<string>(argCount);
                for (int ai = 0; ai < argCount; ai++)
                    args.Add(P(pool, r.ReadUInt32()));
                ushort mthCount = r.ReadUInt16();
                var methods = new List<ExportedMethodDef>(mthCount);
                for (int mi2 = 0; mi2 < mthCount; mi2++)
                    methods.Add(ReadMethodDef(r, pool));
                impls.Add(new ExportedImplDef(targetFq, traitFq, args, methods));
            }
            if (mi < modules.Count && impls.Count > 0)
                modules[mi] = modules[mi] with { Impls = impls };
        }
    }

    private static ExportedMethodDef ReadMethodDef(BinaryReader r, string[] pool)
    {
        string name    = P(pool, r.ReadUInt32());
        string retType = P(pool, r.ReadUInt32());
        string vis     = P(pool, r.ReadUInt32());
        byte flags     = r.ReadByte();
        bool isStatic   = (flags & 0x01) != 0;
        bool isVirtual  = (flags & 0x02) != 0;
        bool isAbstract = (flags & 0x04) != 0;
        ushort minArgs  = r.ReadUInt16();
        byte paramCnt   = r.ReadByte();
        var parms = new List<ExportedParamDef>(paramCnt);
        for (int pi = 0; pi < paramCnt; pi++)
        {
            string pn = P(pool, r.ReadUInt32());
            string pt = P(pool, r.ReadUInt32());
            parms.Add(new ExportedParamDef(pn, pt));
        }
        return new ExportedMethodDef(name, parms, retType, vis, isStatic, isVirtual, isAbstract, minArgs);
    }

    /// L3-G3d: read `where` constraint block (written by ZpkgWriter.WriteTpConstraints).
    /// Returns null when block is absent (older zpkg) or when the count byte is 0.
    private static List<ExportedTypeParamConstraint>? ReadTpConstraints(
        BinaryReader r, MemoryStream ms, string[] pool)
    {
        if (ms.Position >= ms.Length) return null;
        byte count = r.ReadByte();
        if (count == 0) return null;
        var result = new List<ExportedTypeParamConstraint>(count);
        for (int i = 0; i < count; i++)
        {
            string tp = P(pool, r.ReadUInt32());
            byte flags = r.ReadByte();
            byte ifaceCount = r.ReadByte();
            var ifaces = new List<string>(ifaceCount);
            for (int j = 0; j < ifaceCount; j++)
                ifaces.Add(P(pool, r.ReadUInt32()));
            string? baseCls = (flags & 0x04) != 0 ? P(pool, r.ReadUInt32()) : null;
            string? tpRef   = (flags & 0x08) != 0 ? P(pool, r.ReadUInt32()) : null;
            result.Add(new ExportedTypeParamConstraint(
                tp, ifaces, baseCls, tpRef,
                (flags & 0x01) != 0, (flags & 0x02) != 0,
                (flags & 0x10) != 0,
                (flags & 0x20) != 0));
        }
        return result;
    }
}
