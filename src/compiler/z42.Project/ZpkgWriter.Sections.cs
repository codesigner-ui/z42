using System.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// 基础 zpkg 段写入器（META / NSPC / EXPT / DEPS / SIGS / MODS / FILE）+ 字符串
/// 内联工具方法。与 ZpkgWriter.cs（入口/装配）和 ZpkgWriter.Tsig.cs（类型签名）
/// 配套。
public static partial class ZpkgWriter
{
    // ── String interning helpers ──────────────────────────────────────────────

    private static void InternZpkgStrings(StringPool pool, ZpkgFile zpkg)
    {
        foreach (var ns in zpkg.Namespaces)   pool.Intern(ns);
        foreach (var e  in zpkg.Exports)      pool.Intern(e.Symbol);
        foreach (var dep in zpkg.Dependencies)
        {
            pool.Intern(dep.File);
            foreach (var ns in dep.Namespaces) pool.Intern(ns);
        }
        if (zpkg.ExportedModules != null)
            foreach (var mod in zpkg.ExportedModules)
                InternTsigStrings(pool, mod);
    }

    private static void InternTsigStrings(StringPool pool, ExportedModule mod)
    {
        pool.Intern(mod.Namespace);
        foreach (var cls in mod.Classes)
        {
            pool.Intern(cls.Name);
            if (cls.BaseClass != null) pool.Intern(cls.BaseClass);
            foreach (var iface in cls.Interfaces) pool.Intern(iface);
            foreach (var f in cls.Fields)  { pool.Intern(f.Name); pool.Intern(f.TypeName); pool.Intern(f.Visibility); }
            foreach (var m in cls.Methods) InternMethodStrings(pool, m);
            // L3-G4d: TypeParams
            if (cls.TypeParams != null)
                foreach (var tp in cls.TypeParams) pool.Intern(tp);
            // L3-G3d: TypeParamConstraints
            InternTpConstraints(pool, cls.TypeParamConstraints);
        }
        foreach (var iface in mod.Interfaces)
        {
            pool.Intern(iface.Name);
            foreach (var m in iface.Methods) InternMethodStrings(pool, m);
            if (iface.TypeParams is { } itp)
                foreach (var tpName in itp) pool.Intern(tpName);
        }
        foreach (var en in mod.Enums)
        {
            pool.Intern(en.Name);
            foreach (var m in en.Members) pool.Intern(m.Name);
        }
        foreach (var fn in mod.Functions)
        {
            pool.Intern(fn.Name); pool.Intern(fn.ReturnType);
            foreach (var p in fn.Params) { pool.Intern(p.Name); pool.Intern(p.TypeName); }
            // L3-G3d: generic type params + constraints
            if (fn.TypeParams != null)
                foreach (var tp in fn.TypeParams) pool.Intern(tp);
            InternTpConstraints(pool, fn.TypeParamConstraints);
        }
        // L3-Impl2: intern strings used by IMPL section
        if (mod.Impls != null)
            foreach (var impl in mod.Impls)
            {
                pool.Intern(impl.TargetFqName);
                pool.Intern(impl.TraitFqName);
                foreach (var arg in impl.TraitTypeArgs) pool.Intern(arg);
                foreach (var m in impl.Methods) InternMethodStrings(pool, m);
            }
        // 2026-05-02 add-generic-delegates (D1c): intern delegate strings.
        if (mod.Delegates != null)
            foreach (var d in mod.Delegates)
            {
                pool.Intern(d.Name);
                pool.Intern(d.ReturnType);
                foreach (var p in d.Params) { pool.Intern(p.Name); pool.Intern(p.TypeName); }
                if (d.TypeParams != null)
                    foreach (var tp in d.TypeParams) pool.Intern(tp);
                if (d.ContainerClass != null) pool.Intern(d.ContainerClass);
            }
    }

    private static void InternTpConstraints(
        StringPool pool, List<ExportedTypeParamConstraint>? constraints)
    {
        if (constraints is null) return;
        foreach (var c in constraints)
        {
            pool.Intern(c.TypeParam);
            foreach (var i in c.Interfaces) pool.Intern(i);
            if (c.BaseClass    != null) pool.Intern(c.BaseClass);
            if (c.TypeParamRef != null) pool.Intern(c.TypeParamRef);
        }
    }

    private static void InternMethodStrings(StringPool pool, ExportedMethodDef m)
    {
        pool.Intern(m.Name); pool.Intern(m.ReturnType); pool.Intern(m.Visibility);
        foreach (var p in m.Params) { pool.Intern(p.Name); pool.Intern(p.TypeName); }
    }

    // ── META section ─────────────────────────────────────────────────────────

    private static byte[] BuildMetaSection(ZpkgFile zpkg)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        void WriteStr(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        WriteStr(zpkg.Name);
        WriteStr(zpkg.Version);
        WriteStr(zpkg.Entry ?? string.Empty);   // len=0 → no entry (lib)

        return ms.ToArray();
    }

    // ── NSPC section ─────────────────────────────────────────────────────────

    private static byte[] BuildNspcSection(IReadOnlyList<string> namespaces, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)namespaces.Count);
        foreach (var ns in namespaces) w.Write((uint)pool.Idx(ns));
        return ms.ToArray();
    }

    // ── EXPT section ─────────────────────────────────────────────────────────

    private static byte[] BuildExptSection(IReadOnlyList<ZpkgExport> exports, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)exports.Count);
        foreach (var e in exports)
        {
            w.Write((uint)pool.Idx(e.Symbol));
            w.Write(KindByte(e.Kind));
        }
        return ms.ToArray();
    }

    private static byte KindByte(string kind) => kind switch
    {
        "type"  => 1,
        "const" => 2,
        _       => 0,   // "func" (default)
    };

    // ── DEPS section ─────────────────────────────────────────────────────────

    private static byte[] BuildDepsSection(IReadOnlyList<ZpkgDep> deps, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)deps.Count);
        foreach (var dep in deps)
        {
            w.Write((uint)pool.Idx(dep.File));
            w.Write((ushort)dep.Namespaces.Count);
            foreach (var ns in dep.Namespaces) w.Write((uint)pool.Idx(ns));
        }
        return ms.ToArray();
    }

    // ── SIGS section (packed: global function signatures) ─────────────────────

    private static byte[] BuildSigsSection(IReadOnlyList<ZbcFile> modules, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        uint total = (uint)modules.Sum(m => m.Module.Functions.Count);
        w.Write(total);

        foreach (var zbc in modules)
            foreach (var fn in zbc.Module.Functions)
            {
                w.Write((uint)pool.Idx(fn.Name));
                w.Write((ushort)fn.ParamCount);
                w.Write(TypeTags.FromString(fn.RetType));
                w.Write(ExecModes.FromString(fn.ExecMode));
                w.Write((byte)(fn.IsStatic ? 1 : 0));
                // Generic type parameters
                byte tpCount = (byte)(fn.TypeParams?.Count ?? 0);
                w.Write(tpCount);
                if (fn.TypeParams != null)
                    foreach (var tp in fn.TypeParams)
                        w.Write((uint)pool.Idx(tp));
            }

        return ms.ToArray();
    }

    // ── MODS section (packed: per-module FUNC+TYPE bodies) ────────────────────

    private static byte[] BuildModsSection(
        IReadOnlyList<ZbcFile> modules,
        StringPool pool,
        int[][] remaps)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)modules.Count);

        uint firstSigIdx = 0;
        for (int mi = 0; mi < modules.Count; mi++)
        {
            var zbc  = modules[mi];
            var mod  = zbc.Module;

            // FUNC section bytes using global pool indices
            byte[] funcData = ZbcWriter.BuildFuncSection(mod.Functions, pool, remaps[mi]);
            // TYPE section bytes (0 bytes if no classes)
            byte[] typeData = mod.Classes.Count > 0
                ? ZbcWriter.BuildTypeSection(mod.Classes, pool)
                : [];

            w.Write((uint)pool.Idx(zbc.Namespace));
            w.Write((uint)pool.Idx(zbc.SourceFile));
            w.Write((uint)pool.Idx(zbc.SourceHash));
            w.Write((ushort)mod.Functions.Count);
            w.Write(firstSigIdx);
            w.Write((uint)funcData.Length);
            w.Write(funcData);
            w.Write((uint)typeData.Length);
            if (typeData.Length > 0) w.Write(typeData);

            firstSigIdx += (uint)mod.Functions.Count;
        }

        return ms.ToArray();
    }

    // ── FILE section (indexed: per-file path references) ──────────────────────

    private static byte[] BuildFileSection(IReadOnlyList<ZpkgFileEntry> files, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)files.Count);
        foreach (var f in files)
        {
            w.Write((uint)pool.Idx(f.Source));
            w.Write((uint)pool.Idx(f.Bytecode));
            w.Write((uint)pool.Idx(f.SourceHash));
            w.Write((ushort)f.Exports.Count);
            foreach (var e in f.Exports) w.Write((uint)pool.Idx(e));
        }
        return ms.ToArray();
    }
}
