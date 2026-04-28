using System.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// TSIG（类型签名 — 跨 zpkg 引用编译用）+ IMPL（L3-Impl2 跨 zpkg `impl Trait
/// for Type` 声明）+ 共享方法/约束写入工具。与 ZpkgWriter.Sections.cs 的基础
/// 段写入分离。
public static partial class ZpkgWriter
{
    // ── TSIG section (type signatures for reference compilation) ────────────

    private static byte[] BuildTsigSection(List<ExportedModule> modules, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((ushort)modules.Count);
        foreach (var mod in modules)
        {
            w.Write((uint)pool.Idx(mod.Namespace));

            // Classes
            w.Write((ushort)mod.Classes.Count);
            foreach (var cls in mod.Classes)
            {
                w.Write((uint)pool.Idx(cls.Name));
                w.Write(cls.BaseClass != null ? (uint)pool.Idx(cls.BaseClass) : uint.MaxValue);
                byte flags = 0;
                if (cls.IsAbstract) flags |= 0x01;
                if (cls.IsSealed)   flags |= 0x02;
                if (cls.IsStatic)   flags |= 0x04;
                w.Write(flags);

                w.Write((ushort)cls.Fields.Count);
                foreach (var f in cls.Fields)
                {
                    w.Write((uint)pool.Idx(f.Name));
                    w.Write((uint)pool.Idx(f.TypeName));
                    w.Write((uint)pool.Idx(f.Visibility));
                    w.Write((byte)(f.IsStatic ? 1 : 0));
                }

                w.Write((ushort)cls.Methods.Count);
                foreach (var m in cls.Methods)
                    WriteMethodDef(w, m, pool);

                w.Write((ushort)cls.Interfaces.Count);
                foreach (var iface in cls.Interfaces)
                    w.Write((uint)pool.Idx(iface));

                // L3-G4d: generic type parameter names
                byte tpCount = (byte)(cls.TypeParams?.Count ?? 0);
                w.Write(tpCount);
                if (cls.TypeParams != null)
                    foreach (var tp in cls.TypeParams)
                        w.Write((uint)pool.Idx(tp));

                // L3-G3d: per-type-param `where` constraints
                WriteTpConstraints(w, cls.TypeParamConstraints, pool);
            }

            // Interfaces
            w.Write((ushort)mod.Interfaces.Count);
            foreach (var iface in mod.Interfaces)
            {
                w.Write((uint)pool.Idx(iface.Name));
                w.Write((ushort)iface.Methods.Count);
                foreach (var m in iface.Methods)
                    WriteMethodDef(w, m, pool);
                // L3 primitive-as-struct: interface's own type params (e.g. `T` for
                // `INumber<T>`) — consumer side restores T occurrences as generic.
                int tpCount = iface.TypeParams?.Count ?? 0;
                w.Write((byte)tpCount);
                if (iface.TypeParams is { } itp)
                    foreach (var tpName in itp)
                        w.Write((uint)pool.Idx(tpName));
            }

            // Enums
            w.Write((ushort)mod.Enums.Count);
            foreach (var en in mod.Enums)
            {
                w.Write((uint)pool.Idx(en.Name));
                w.Write((ushort)en.Members.Count);
                foreach (var m in en.Members)
                {
                    w.Write((uint)pool.Idx(m.Name));
                    w.Write(m.Value);
                }
            }

            // Functions
            w.Write((ushort)mod.Functions.Count);
            foreach (var fn in mod.Functions)
            {
                w.Write((uint)pool.Idx(fn.Name));
                w.Write((uint)pool.Idx(fn.ReturnType));
                w.Write((ushort)fn.MinArgCount);
                w.Write((byte)fn.Params.Count);
                foreach (var p in fn.Params)
                {
                    w.Write((uint)pool.Idx(p.Name));
                    w.Write((uint)pool.Idx(p.TypeName));
                }
                // L3-G3d: generic type parameter names + `where` constraints.
                byte fnTpCount = (byte)(fn.TypeParams?.Count ?? 0);
                w.Write(fnTpCount);
                if (fn.TypeParams != null)
                    foreach (var tp in fn.TypeParams)
                        w.Write((uint)pool.Idx(tp));
                WriteTpConstraints(w, fn.TypeParamConstraints, pool);
            }
        }

        return ms.ToArray();
    }

    /// L3-G3d: append `where` constraint block for a class or function.
    /// Layout: u8 count, { u32 tpName, u8 flags, u8 ifaceCount, u32* ifaceNames,
    ///                     [u32 baseCls?], [u32 tpRef?] }.
    /// Flags: 0x01 RequiresClass, 0x02 RequiresStruct, 0x04 HasBaseClass, 0x08 HasTpRef,
    ///        0x10 RequiresConstructor (L3-G2.5 ctor), 0x20 RequiresEnum (L3-G2.5 enum).
    private static void WriteTpConstraints(
        BinaryWriter w,
        List<ExportedTypeParamConstraint>? constraints,
        StringPool pool)
    {
        if (constraints is null || constraints.Count == 0)
        {
            w.Write((byte)0);
            return;
        }
        w.Write((byte)constraints.Count);
        foreach (var c in constraints)
        {
            w.Write((uint)pool.Idx(c.TypeParam));
            byte flags = 0;
            if (c.RequiresClass)           flags |= 0x01;
            if (c.RequiresStruct)          flags |= 0x02;
            if (c.BaseClass != null)       flags |= 0x04;
            if (c.TypeParamRef != null)    flags |= 0x08;
            if (c.RequiresConstructor)     flags |= 0x10;
            if (c.RequiresEnum)            flags |= 0x20;
            w.Write(flags);
            w.Write((byte)c.Interfaces.Count);
            foreach (var iname in c.Interfaces)
                w.Write((uint)pool.Idx(iname));
            if (c.BaseClass != null)
                w.Write((uint)pool.Idx(c.BaseClass));
            if (c.TypeParamRef != null)
                w.Write((uint)pool.Idx(c.TypeParamRef));
        }
    }

    // ── IMPL section (L3-Impl2: cross-zpkg `impl Trait for Type` declarations) ──

    /// Layout: u16 module count, per module: u32 namespace, u16 impl count,
    /// per impl: u32 targetFq, u32 traitFq, u8 traitArgCount, u32* traitArgs,
    ///           u16 methodCount, methodDef*. Mirrors TSIG module ordering.
    private static byte[] BuildImplSection(List<ExportedModule> modules, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((ushort)modules.Count);
        foreach (var mod in modules)
        {
            w.Write((uint)pool.Idx(mod.Namespace));
            var impls = mod.Impls ?? new List<ExportedImplDef>();
            w.Write((ushort)impls.Count);
            foreach (var impl in impls)
            {
                w.Write((uint)pool.Idx(impl.TargetFqName));
                w.Write((uint)pool.Idx(impl.TraitFqName));
                w.Write((byte)impl.TraitTypeArgs.Count);
                foreach (var arg in impl.TraitTypeArgs)
                    w.Write((uint)pool.Idx(arg));
                w.Write((ushort)impl.Methods.Count);
                foreach (var m in impl.Methods)
                    WriteMethodDef(w, m, pool);
            }
        }
        return ms.ToArray();
    }

    private static void WriteMethodDef(BinaryWriter w, ExportedMethodDef m, StringPool pool)
    {
        w.Write((uint)pool.Idx(m.Name));
        w.Write((uint)pool.Idx(m.ReturnType));
        w.Write((uint)pool.Idx(m.Visibility));
        byte flags = 0;
        if (m.IsStatic)   flags |= 0x01;
        if (m.IsVirtual)  flags |= 0x02;
        if (m.IsAbstract) flags |= 0x04;
        w.Write(flags);
        w.Write((ushort)m.MinArgCount);
        w.Write((byte)m.Params.Count);
        foreach (var p in m.Params)
        {
            w.Write((uint)pool.Idx(p.Name));
            w.Write((uint)pool.Idx(p.TypeName));
        }
    }
}
