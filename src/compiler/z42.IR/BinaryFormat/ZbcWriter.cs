using System.Text;

// ReSharper disable SwitchStatementHandlesSomeKnownEnumValuesWithDefault

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Serializes an <see cref="IrModule"/> into binary zbc format (v0.3).
///
/// File layout:
///   Header  (16 bytes): magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
///   Directory (sec_count × 12 bytes): tag[4] + offset[4] + size[4]
///   Sections (at absolute offsets recorded in directory):
///
///   Full mode (flags.STRIPPED = 0):
///     NSPC  namespace string
///     STRS  unified string heap
///     TYPE  class descriptors
///     SIGS  function signature table (name + params + ret + exec_mode)
///     IMPT  import table (external symbol names)
///     EXPT  export table
///     FUNC  function bodies (indexed by position)
///
///   Stripped mode (flags.STRIPPED = 1):
///     NSPC  namespace string
///     BSTR  body-only string heap (subset of STRS)
///     FUNC  function bodies (same format as full mode)
/// </summary>
public static partial class ZbcWriter
{
    public const ushort VersionMajor = 0;
    public const ushort VersionMinor = 8;   // 2026-04-26 cross-zpkg-impl-propagation: zpkg IMPL section carries cross-zpkg `impl Trait for Type` declarations

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="module"/> into binary zbc format.
    /// Pass <see cref="ZbcFlags.Stripped"/> to produce a minimal .cache/ file
    /// (metadata omitted; zpkg index required for dispatch).
    /// </summary>
    public static byte[] Write(
        IrModule             module,
        ZbcFlags             flags   = ZbcFlags.None,
        IEnumerable<string>? exports = null)
    {
        bool stripped = flags.HasFlag(ZbcFlags.Stripped);

        var exportSet = exports is null
            ? module.Functions.Select(f => f.Name).ToHashSet()
            : exports.ToHashSet();

        // ── Build string pool ─────────────────────────────────────────────────
        var pool     = new StringPool();
        var strRemap = new int[module.StringPool.Count];
        InternPoolStrings(pool, module, strRemap, fullMode: !stripped);

        // ── Build sections ────────────────────────────────────────────────────
        var sections = new List<(byte[] Tag, byte[] Data)>();

        sections.Add((SectionTags.Nspc, BuildNspcSection(module.Name)));

        if (stripped)
        {
            sections.Add((SectionTags.Bstr, BuildStrpSection(pool)));
            sections.Add((SectionTags.Func, BuildFuncSection(module.Functions, pool, strRemap)));
        }
        else
        {
            sections.Add((SectionTags.Strs, BuildStrpSection(pool)));
            sections.Add((SectionTags.Type, BuildTypeSection(module.Classes, pool)));
            sections.Add((SectionTags.Sigs, BuildSigsSection(module.Functions, pool)));
            sections.Add((SectionTags.Impt, BuildImptSection(module, pool)));
            sections.Add((SectionTags.Expt, BuildExptSection(module.Functions, pool, exportSet)));
            sections.Add((SectionTags.Func, BuildFuncSection(module.Functions, pool, strRemap)));

            // DBUG section: line table + local variable names
            bool hasDebug = module.Functions.Any(f =>
                f.LineTable is { Count: > 0 } || f.LocalVarTable is { Count: > 0 });
            if (hasDebug)
            {
                flags |= ZbcFlags.HasDebug;
                sections.Add((SectionTags.Dbug, BuildDbugSection(module.Functions, pool)));
            }
        }

        return AssembleFile(flags, sections);
    }

    // ── File assembly (header + directory + sections) ─────────────────────────

    /// Assembles a complete binary file: 16-byte header + section directory + section data.
    /// All section offsets in the directory are absolute from file start.
    internal static byte[] AssembleFile(ZbcFlags flags, List<(byte[] Tag, byte[] Data)> sections)
    {
        int headerSize = 16;
        int dirSize    = sections.Count * 12; // tag[4] + offset[4] + size[4]
        uint nextOffset = (uint)(headerSize + dirSize);

        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        WriteHeader(w, flags, (ushort)sections.Count);

        // Directory
        foreach (var (tag, data) in sections)
        {
            w.Write(tag);
            w.Write(nextOffset);
            w.Write((uint)data.Length);
            nextOffset += (uint)data.Length;
        }

        // Section data
        foreach (var (_, data) in sections)
            w.Write(data);

        w.Flush();
        return ms.ToArray();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private static void WriteHeader(BinaryWriter w, ZbcFlags flags, ushort secCount)
    {
        w.Write((byte)'Z'); w.Write((byte)'B'); w.Write((byte)'C'); w.Write((byte)'\0');
        w.Write(VersionMajor);
        w.Write(VersionMinor);
        w.Write((ushort)flags);
        w.Write(secCount);   // was reserved[2] in v0.2
        w.Write((uint)0);    // reserved[4]
    }

    // ── String pool ───────────────────────────────────────────────────────────

    /// <summary>
    /// Interns all strings needed for the chosen mode.
    /// Full mode: every string in the module (names, types, instruction refs).
    /// Stripped mode: only strings referenced inside function bodies.
    /// </summary>
    public static void InternPoolStrings(
        StringPool pool, IrModule module, int[] strRemap, bool fullMode)
    {
        if (fullMode)
        {
            pool.Intern(module.Name);

            for (int i = 0; i < module.StringPool.Count; i++)
                strRemap[i] = pool.Intern(module.StringPool[i]);

            foreach (var cls in module.Classes)
            {
                pool.Intern(cls.Name);
                if (cls.BaseClass != null) pool.Intern(cls.BaseClass);
                foreach (var fld in cls.Fields) { pool.Intern(fld.Name); pool.Intern(fld.Type); }
                if (cls.TypeParams != null)
                    foreach (var tp in cls.TypeParams)
                        pool.Intern(tp);
                if (cls.TypeParamConstraints != null)
                    foreach (var b in cls.TypeParamConstraints)
                        InternConstraintBundle(pool, b);
            }

            foreach (var fn in module.Functions)
            {
                pool.Intern(fn.Name);
                pool.Intern(fn.RetType);
                foreach (var block in fn.Blocks)
                {
                    pool.Intern(block.Label);
                    foreach (var instr in block.Instructions) InternInstrStrings(pool, instr);
                }
                if (fn.ExceptionTable != null)
                    foreach (var exc in fn.ExceptionTable)
                    {
                        pool.Intern(exc.TryStart); pool.Intern(exc.TryEnd);
                        pool.Intern(exc.CatchLabel);
                        if (exc.CatchType != null) pool.Intern(exc.CatchType);
                    }
                if (fn.LineTable != null)
                    foreach (var le in fn.LineTable)
                        if (le.File != null) pool.Intern(le.File);
                if (fn.LocalVarTable != null)
                    foreach (var lv in fn.LocalVarTable)
                        pool.Intern(lv.Name);
                if (fn.TypeParams != null)
                    foreach (var tp in fn.TypeParams)
                        pool.Intern(tp);
                if (fn.TypeParamConstraints != null)
                    foreach (var b in fn.TypeParamConstraints)
                        InternConstraintBundle(pool, b);
            }
        }
        else
        {
            // Stripped mode: body strings + 运行时仍需要的 LineTable.File（用于异常 stack trace）。
            // BuildFuncSection 无条件写 LineTable（含 File），所以 stripped 模式也必须 intern。
            for (int i = 0; i < module.StringPool.Count; i++)
                strRemap[i] = pool.Intern(module.StringPool[i]);

            foreach (var fn in module.Functions)
            {
                foreach (var block in fn.Blocks)
                    foreach (var instr in block.Instructions) InternInstrStrings(pool, instr);

                if (fn.ExceptionTable != null)
                    foreach (var exc in fn.ExceptionTable)
                        if (exc.CatchType != null) pool.Intern(exc.CatchType);

                // 与 fullMode 分支保持一致：LineTable.File 必须 intern
                if (fn.LineTable != null)
                    foreach (var le in fn.LineTable)
                        if (le.File != null) pool.Intern(le.File);
            }
        }
    }

    // ── NSPC section ──────────────────────────────────────────────────────────

    internal static byte[] BuildNspcSection(string ns)
    {
        var utf8 = Encoding.UTF8.GetBytes(ns);
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((ushort)utf8.Length);
        w.Write(utf8);
        return ms.ToArray();
    }

    // ── STRS / BSTR section (string heap) ────────────────────────────────────

    public static byte[] BuildStrpSection(StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        var strings = pool.AllStrings;
        var encoded = strings.Select(Encoding.UTF8.GetBytes).ToArray();

        w.Write((uint)encoded.Length);

        // Entry table: [offset:u32][len:u32]
        uint offset = 0;
        foreach (var b in encoded) { w.Write(offset); w.Write((uint)b.Length); offset += (uint)b.Length; }

        // Raw data
        foreach (var b in encoded) w.Write(b);

        return ms.ToArray();
    }

    // ── TYPE section ──────────────────────────────────────────────────────────

    public static byte[] BuildTypeSection(List<IrClassDesc> classes, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)classes.Count);
        foreach (var cls in classes)
        {
            w.Write((uint)pool.Idx(cls.Name));
            w.Write(cls.BaseClass != null ? (uint)pool.Idx(cls.BaseClass) : uint.MaxValue);
            w.Write((ushort)cls.Fields.Count);
            foreach (var fld in cls.Fields)
            {
                w.Write((uint)pool.Idx(fld.Name));
                w.Write(TypeTags.FromString(fld.Type));
            }
            // Generic type parameters for this class (L3-G1) + per-tp constraints (L3-G3a)
            var tpCount = (byte)(cls.TypeParams?.Count ?? 0);
            w.Write(tpCount);
            if (cls.TypeParams != null)
                for (int i = 0; i < cls.TypeParams.Count; i++)
                {
                    w.Write((uint)pool.Idx(cls.TypeParams[i]));
                    var b = cls.TypeParamConstraints != null && i < cls.TypeParamConstraints.Count
                        ? cls.TypeParamConstraints[i] : null;
                    WriteConstraintBundle(w, pool, b);
                }
        }

        return ms.ToArray();
    }

    // ── SIGS section (full mode: function signatures) ─────────────────────────

    internal static byte[] BuildSigsSection(List<IrFunction> functions, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)functions.Count);
        foreach (var fn in functions)
        {
            w.Write((uint)pool.Idx(fn.Name));
            w.Write((ushort)fn.ParamCount);
            w.Write(TypeTags.FromString(fn.RetType));
            w.Write(ExecModes.FromString(fn.ExecMode));
            w.Write((byte)(fn.IsStatic ? 1 : 0));  // is_static flag
            // Generic type parameters (L3-G1) + per-tp constraints (L3-G3a)
            byte tpCount = (byte)(fn.TypeParams?.Count ?? 0);
            w.Write(tpCount);
            if (fn.TypeParams != null)
                for (int i = 0; i < fn.TypeParams.Count; i++)
                {
                    w.Write((uint)pool.Idx(fn.TypeParams[i]));
                    var b = fn.TypeParamConstraints != null && i < fn.TypeParamConstraints.Count
                        ? fn.TypeParamConstraints[i] : null;
                    WriteConstraintBundle(w, pool, b);
                }
        }

        return ms.ToArray();
    }

    // ── Constraint bundle codec (L3-G3a) ──────────────────────────────────────

    /// Interns all strings referenced by a constraint bundle so they land in the pool
    /// before SIGS / TYPE encoding. Null-safe.
    internal static void InternConstraintBundle(StringPool pool, IrConstraintBundle? b)
    {
        if (b is null) return;
        if (b.BaseClass is not null) pool.Intern(b.BaseClass);
        foreach (var i in b.Interfaces) pool.Intern(i);
        if (b.TypeParamConstraint is not null) pool.Intern(b.TypeParamConstraint);
    }

    /// Encodes one constraint bundle. Null input is treated as fully empty
    /// (flags=0, interface_count=0).
    /// Layout (v0.7): flags u8 (bit0 class, bit1 struct, bit2 HasBaseClass,
    ///                bit3 HasTypeParamConstraint, bit4 RequiresConstructor,
    ///                bit5 RequiresEnum),
    ///                [if bit2] base_class u32, [if bit3] type_param_constraint u32,
    ///                interface_count u8, interface_name_idx[] u32.
    internal static void WriteConstraintBundle(BinaryWriter w, StringPool pool, IrConstraintBundle? b)
    {
        byte flags = 0;
        if (b is not null)
        {
            if (b.RequiresClass)                   flags |= 0x01;
            if (b.RequiresStruct)                  flags |= 0x02;
            if (b.BaseClass is not null)           flags |= 0x04;
            if (b.TypeParamConstraint is not null) flags |= 0x08;
            if (b.RequiresConstructor)             flags |= 0x10;
            if (b.RequiresEnum)                    flags |= 0x20;
        }
        w.Write(flags);
        if (b is not null && b.BaseClass is not null)
            w.Write((uint)pool.Idx(b.BaseClass));
        if (b is not null && b.TypeParamConstraint is not null)
            w.Write((uint)pool.Idx(b.TypeParamConstraint));
        int interfaceCount = b?.Interfaces.Count ?? 0;
        w.Write((byte)interfaceCount);
        if (b is not null)
            foreach (var iface in b.Interfaces)
                w.Write((uint)pool.Idx(iface));
    }

    // ── IMPT section (full mode: import table) ────────────────────────────────

    private static byte[] BuildImptSection(IrModule module, StringPool pool)
    {
        var definedNames = module.Functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var imports      = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fn in module.Functions)
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    switch (instr)
                    {
                        case CallInstr i when !definedNames.Contains(i.Func):
                            imports.Add(i.Func); break;
                        case BuiltinInstr i:
                            imports.Add(i.Name); break;
                    }

        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)imports.Count);
        foreach (var imp in imports) w.Write((uint)pool.Idx(imp));

        return ms.ToArray();
    }

    // ── EXPT section (full mode: export table) ────────────────────────────────

    private static byte[] BuildExptSection(
        List<IrFunction> functions, StringPool pool, HashSet<string> exportSet)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        var exported = functions.Where(f => exportSet.Contains(f.Name)).ToList();
        w.Write((uint)exported.Count);
        foreach (var fn in exported)
        {
            w.Write((uint)pool.Idx(fn.Name));
            w.Write((byte)0); // kind: 0 = func
        }

        return ms.ToArray();
    }

    // ── FUNC section (function bodies, both modes) ────────────────────────────

    public static byte[] BuildFuncSection(
        List<IrFunction> functions, StringPool pool, int[] strRemap)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)functions.Count);

        foreach (var fn in functions)
        {
            var blockIdx = new Dictionary<string, ushort>(fn.Blocks.Count);
            for (int i = 0; i < fn.Blocks.Count; i++)
                blockIdx[fn.Blocks[i].Label] = (ushort)i;

            using var instrMs = new MemoryStream();
            using var iw      = new BinaryWriter(instrMs, Encoding.UTF8, leaveOpen: false);

            var blockOffsets = new uint[fn.Blocks.Count];
            for (int bi = 0; bi < fn.Blocks.Count; bi++)
            {
                blockOffsets[bi] = (uint)instrMs.Position;
                var block = fn.Blocks[bi];
                foreach (var instr in block.Instructions)
                    WriteInstr(iw, instr, pool, strRemap, blockIdx);
                WriteTerminator(iw, block.Terminator, blockIdx);
            }
            iw.Flush();
            var instrBytes = instrMs.ToArray();

            int excCount  = fn.ExceptionTable?.Count ?? 0;
            int lineCount = fn.LineTable?.Count ?? 0;
            int regCount  = ComputeRegCount(fn);

            w.Write((ushort)regCount);
            w.Write((ushort)fn.Blocks.Count);
            w.Write((uint)instrBytes.Length);
            w.Write((ushort)excCount);
            w.Write((ushort)lineCount);

            foreach (var off in blockOffsets) w.Write(off);

            if (fn.ExceptionTable != null)
                foreach (var exc in fn.ExceptionTable)
                {
                    w.Write(blockIdx.TryGetValue(exc.TryStart,   out var ts) ? ts : (ushort)0);
                    w.Write(blockIdx.TryGetValue(exc.TryEnd,     out var te) ? te : (ushort)fn.Blocks.Count);
                    w.Write(blockIdx.TryGetValue(exc.CatchLabel, out var cl) ? cl : (ushort)0);
                    w.Write(exc.CatchType != null ? (uint)pool.Idx(exc.CatchType) : uint.MaxValue);
                    w.Write((ushort)exc.CatchReg.Id);
                }

            if (fn.LineTable != null)
                foreach (var le in fn.LineTable)
                {
                    w.Write((ushort)le.BlockIdx);
                    w.Write((ushort)le.InstrIdx);
                    w.Write((uint)le.Line);
                    w.Write(le.File != null ? (uint)pool.Idx(le.File) : uint.MaxValue);
                }

            w.Write(instrBytes);
        }

        return ms.ToArray();
    }

    // ── DBUG section (debug info: local variable names) ──────────────────────

    private static byte[] BuildDbugSection(List<IrFunction> functions, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)functions.Count);

        foreach (var fn in functions)
        {
            int varCount = fn.LocalVarTable?.Count ?? 0;
            w.Write((ushort)varCount);
            if (fn.LocalVarTable != null)
                foreach (var lv in fn.LocalVarTable)
                {
                    w.Write((uint)pool.Idx(lv.Name));
                    w.Write((ushort)lv.RegId);
                }
        }

        return ms.ToArray();
    }

}
