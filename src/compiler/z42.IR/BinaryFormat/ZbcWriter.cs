using System.Text;

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
public static class ZbcWriter
{
    public const ushort VersionMajor = 0;
    public const ushort VersionMinor = 4;

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
            }
        }
        else
        {
            // Stripped mode: only body strings
            for (int i = 0; i < module.StringPool.Count; i++)
                strRemap[i] = pool.Intern(module.StringPool[i]);

            foreach (var fn in module.Functions)
            {
                foreach (var block in fn.Blocks)
                    foreach (var instr in block.Instructions) InternInstrStrings(pool, instr);

                if (fn.ExceptionTable != null)
                    foreach (var exc in fn.ExceptionTable)
                        if (exc.CatchType != null) pool.Intern(exc.CatchType);
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
        }

        return ms.ToArray();
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

            int excCount = fn.ExceptionTable?.Count ?? 0;
            int regCount = ComputeRegCount(fn);

            w.Write((ushort)regCount);
            w.Write((ushort)fn.Blocks.Count);
            w.Write((uint)instrBytes.Length);
            w.Write((ushort)excCount);

            foreach (var off in blockOffsets) w.Write(off);

            if (fn.ExceptionTable != null)
                foreach (var exc in fn.ExceptionTable)
                {
                    w.Write(blockIdx.TryGetValue(exc.TryStart,   out var ts) ? ts : (ushort)0);
                    w.Write(blockIdx.TryGetValue(exc.TryEnd,     out var te) ? te : (ushort)fn.Blocks.Count);
                    w.Write(blockIdx.TryGetValue(exc.CatchLabel, out var cl) ? cl : (ushort)0);
                    w.Write(exc.CatchType != null ? (uint)pool.Idx(exc.CatchType) : uint.MaxValue);
                    w.Write((ushort)exc.CatchReg);
                }

            w.Write(instrBytes);
        }

        return ms.ToArray();
    }

    // ── Instruction encoding ──────────────────────────────────────────────────

    private const ushort NoReg = 0xFFFF;

    private static void WriteInstr(
        BinaryWriter w, IrInstr instr,
        StringPool pool, int[] strRemap,
        Dictionary<string, ushort> blockIdx)
    {
        switch (instr)
        {
            case ConstStrInstr i:
                w.Write(Opcodes.ConstStr); w.Write(TypeTags.Str); w.Write((ushort)i.Dst);
                w.Write((uint)strRemap[i.Idx]);
                break;
            case ConstI32Instr i:
                w.Write(Opcodes.ConstI); w.Write(TypeTags.I32); w.Write((ushort)i.Dst);
                w.Write(i.Val);
                break;
            case ConstI64Instr i:
                w.Write(Opcodes.ConstI); w.Write(TypeTags.I64); w.Write((ushort)i.Dst);
                w.Write(i.Val);
                break;
            case ConstF64Instr i:
                w.Write(Opcodes.ConstF); w.Write(TypeTags.F64); w.Write((ushort)i.Dst);
                w.Write(i.Val);
                break;
            case ConstBoolInstr i:
                w.Write(Opcodes.ConstBool); w.Write(TypeTags.Bool); w.Write((ushort)i.Dst);
                w.Write((byte)(i.Val ? 1 : 0));
                break;
            case ConstNullInstr i:
                w.Write(Opcodes.ConstNull); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                break;
            case CopyInstr i:
                w.Write(Opcodes.Copy); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((ushort)i.Src);
                break;
            case StoreInstr i:
                w.Write(Opcodes.Store); w.Write(TypeTags.Unknown); w.Write(NoReg);
                w.Write((uint)pool.Idx(i.Var)); w.Write((ushort)i.Src);
                break;
            case LoadInstr i:
                w.Write(Opcodes.Load); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((uint)pool.Idx(i.Var));
                break;

            case AddInstr i:    WriteBin(w, Opcodes.Add,    i.Dst, i.A, i.B); break;
            case SubInstr i:    WriteBin(w, Opcodes.Sub,    i.Dst, i.A, i.B); break;
            case MulInstr i:    WriteBin(w, Opcodes.Mul,    i.Dst, i.A, i.B); break;
            case DivInstr i:    WriteBin(w, Opcodes.Div,    i.Dst, i.A, i.B); break;
            case RemInstr i:    WriteBin(w, Opcodes.Rem,    i.Dst, i.A, i.B); break;
            case AndInstr i:    WriteBin(w, Opcodes.And,    i.Dst, i.A, i.B); break;
            case OrInstr  i:    WriteBin(w, Opcodes.Or,     i.Dst, i.A, i.B); break;
            case BitAndInstr i: WriteBin(w, Opcodes.BitAnd, i.Dst, i.A, i.B); break;
            case BitOrInstr  i: WriteBin(w, Opcodes.BitOr,  i.Dst, i.A, i.B); break;
            case BitXorInstr i: WriteBin(w, Opcodes.BitXor, i.Dst, i.A, i.B); break;
            case ShlInstr i:    WriteBin(w, Opcodes.Shl,    i.Dst, i.A, i.B); break;
            case ShrInstr i:    WriteBin(w, Opcodes.Shr,    i.Dst, i.A, i.B); break;
            case StrConcatInstr i: WriteBin(w, Opcodes.StrConcat, i.Dst, i.A, i.B); break;

            case EqInstr i: WriteBin(w, Opcodes.Eq, i.Dst, i.A, i.B); break;
            case NeInstr i: WriteBin(w, Opcodes.Ne, i.Dst, i.A, i.B); break;
            case LtInstr i: WriteBin(w, Opcodes.Lt, i.Dst, i.A, i.B); break;
            case LeInstr i: WriteBin(w, Opcodes.Le, i.Dst, i.A, i.B); break;
            case GtInstr i: WriteBin(w, Opcodes.Gt, i.Dst, i.A, i.B); break;
            case GeInstr i: WriteBin(w, Opcodes.Ge, i.Dst, i.A, i.B); break;

            case NegInstr    i: WriteUn(w, Opcodes.Neg,    i.Dst, i.Src); break;
            case NotInstr    i: WriteUn(w, Opcodes.Not,    i.Dst, i.Src); break;
            case BitNotInstr i: WriteUn(w, Opcodes.BitNot, i.Dst, i.Src); break;
            case ToStrInstr  i: WriteUn(w, Opcodes.ToStr,  i.Dst, i.Src); break;
            case ArrayLenInstr i: WriteUn(w, Opcodes.ArrayLen, i.Dst, i.Arr); break;

            case CallInstr i:
                w.Write(Opcodes.Call); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((uint)pool.Idx(i.Func));
                WriteArgs(w, i.Args);
                break;
            case BuiltinInstr i:
                w.Write(Opcodes.Builtin); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((uint)pool.Idx(i.Name));
                WriteArgs(w, i.Args);
                break;
            case VCallInstr i:
                w.Write(Opcodes.VCall); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((uint)pool.Idx(i.Method)); w.Write((ushort)i.Obj);
                WriteArgs(w, i.Args);
                break;

            case FieldGetInstr i:
                w.Write(Opcodes.FieldGet); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((ushort)i.Obj); w.Write((uint)pool.Idx(i.FieldName));
                break;
            case FieldSetInstr i:
                w.Write(Opcodes.FieldSet); w.Write(TypeTags.Unknown); w.Write(NoReg);
                w.Write((ushort)i.Obj); w.Write((uint)pool.Idx(i.FieldName)); w.Write((ushort)i.Val);
                break;
            case StaticGetInstr i:
                w.Write(Opcodes.StaticGet); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((uint)pool.Idx(i.Field));
                break;
            case StaticSetInstr i:
                w.Write(Opcodes.StaticSet); w.Write(TypeTags.Unknown); w.Write(NoReg);
                w.Write((uint)pool.Idx(i.Field)); w.Write((ushort)i.Val);
                break;

            case ObjNewInstr i:
                w.Write(Opcodes.ObjNew); w.Write(TypeTags.Object); w.Write((ushort)i.Dst);
                w.Write((uint)pool.Idx(i.ClassName));
                WriteArgs(w, i.Args);
                break;
            case IsInstanceInstr i:
                w.Write(Opcodes.IsInstance); w.Write(TypeTags.Bool); w.Write((ushort)i.Dst);
                w.Write((ushort)i.Obj); w.Write((uint)pool.Idx(i.ClassName));
                break;
            case AsCastInstr i:
                w.Write(Opcodes.AsCast); w.Write(TypeTags.Object); w.Write((ushort)i.Dst);
                w.Write((ushort)i.Obj); w.Write((uint)pool.Idx(i.ClassName));
                break;

            case ArrayNewInstr i:
                w.Write(Opcodes.ArrayNew); w.Write(TypeTags.Array); w.Write((ushort)i.Dst);
                w.Write((ushort)i.Size);
                break;
            case ArrayNewLitInstr i:
                w.Write(Opcodes.ArrayNewLit); w.Write(TypeTags.Array); w.Write((ushort)i.Dst);
                WriteArgs(w, i.Elems);
                break;
            case ArrayGetInstr i:
                w.Write(Opcodes.ArrayGet); w.Write(TypeTags.Unknown); w.Write((ushort)i.Dst);
                w.Write((ushort)i.Arr); w.Write((ushort)i.Idx);
                break;
            case ArraySetInstr i:
                w.Write(Opcodes.ArraySet); w.Write(TypeTags.Unknown); w.Write(NoReg);
                w.Write((ushort)i.Arr); w.Write((ushort)i.Idx); w.Write((ushort)i.Val);
                break;

            default:
                throw new InvalidOperationException($"ZbcWriter: unhandled instruction {instr.GetType().Name}");
        }
    }

    private static void WriteTerminator(
        BinaryWriter w, IrTerminator term, Dictionary<string, ushort> blockIdx)
    {
        switch (term)
        {
            case RetTerm { Reg: null }:
                w.Write(Opcodes.Ret); w.Write(TypeTags.Unknown); w.Write(NoReg);
                break;
            case RetTerm { Reg: int r }:
                w.Write(Opcodes.RetVal); w.Write(TypeTags.Unknown); w.Write((ushort)r);
                break;
            case BrTerm bt:
                w.Write(Opcodes.Br); w.Write(TypeTags.Unknown); w.Write(NoReg);
                w.Write(blockIdx[bt.Label]);
                break;
            case BrCondTerm bc:
                w.Write(Opcodes.BrCond); w.Write(TypeTags.Unknown); w.Write((ushort)bc.Cond);
                w.Write(blockIdx[bc.TrueLabel]); w.Write(blockIdx[bc.FalseLabel]);
                break;
            case ThrowTerm tt:
                w.Write(Opcodes.Throw); w.Write(TypeTags.Unknown); w.Write((ushort)tt.Reg);
                break;
            default:
                throw new InvalidOperationException($"ZbcWriter: unhandled terminator {term.GetType().Name}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteBin(BinaryWriter w, byte op, int dst, int a, int b)
    {
        w.Write(op); w.Write(TypeTags.Unknown); w.Write((ushort)dst);
        w.Write((ushort)a); w.Write((ushort)b);
    }

    private static void WriteUn(BinaryWriter w, byte op, int dst, int src)
    {
        w.Write(op); w.Write(TypeTags.Unknown); w.Write((ushort)dst);
        w.Write((ushort)src);
    }

    private static void WriteArgs(BinaryWriter w, List<int> args)
    {
        w.Write((byte)args.Count);
        foreach (var a in args) w.Write((ushort)a);
    }

    internal static void InternInstrStrings(StringPool pool, IrInstr instr)
    {
        switch (instr)
        {
            case CallInstr i:       pool.Intern(i.Func); break;
            case BuiltinInstr i:    pool.Intern(i.Name); break;
            case VCallInstr i:      pool.Intern(i.Method); break;
            case FieldGetInstr i:   pool.Intern(i.FieldName); break;
            case FieldSetInstr i:   pool.Intern(i.FieldName); break;
            case ObjNewInstr i:     pool.Intern(i.ClassName); break;
            case IsInstanceInstr i: pool.Intern(i.ClassName); break;
            case AsCastInstr i:     pool.Intern(i.ClassName); break;
            case StaticGetInstr i:  pool.Intern(i.Field); break;
            case StaticSetInstr i:  pool.Intern(i.Field); break;
            case StoreInstr i:      pool.Intern(i.Var); break;
            case LoadInstr i:       pool.Intern(i.Var); break;
        }
    }

    private static int ComputeRegCount(IrFunction fn)
    {
        int max = fn.ParamCount - 1;
        void Visit(int r) { if (r >= 0 && r > max) max = r; }
        foreach (var block in fn.Blocks)
        {
            foreach (var instr in block.Instructions) VisitInstrRegs(instr, Visit);
            VisitTermRegs(block.Terminator, Visit);
        }
        return Math.Max(max + 1, fn.ParamCount);
    }

    private static void VisitInstrRegs(IrInstr instr, Action<int> v)
    {
        switch (instr)
        {
            case ConstStrInstr i:  v(i.Dst); break;
            case ConstI32Instr i:  v(i.Dst); break;
            case ConstI64Instr i:  v(i.Dst); break;
            case ConstF64Instr i:  v(i.Dst); break;
            case ConstBoolInstr i: v(i.Dst); break;
            case ConstNullInstr i: v(i.Dst); break;
            case CopyInstr i:  v(i.Dst); v(i.Src); break;
            case StoreInstr i: v(i.Src); break;
            case LoadInstr i:  v(i.Dst); break;
            case AddInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case SubInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case MulInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case DivInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case RemInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case NegInstr i:    v(i.Dst); v(i.Src); break;
            case AndInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case OrInstr  i:    v(i.Dst); v(i.A); v(i.B); break;
            case NotInstr i:    v(i.Dst); v(i.Src); break;
            case BitAndInstr i: v(i.Dst); v(i.A); v(i.B); break;
            case BitOrInstr  i: v(i.Dst); v(i.A); v(i.B); break;
            case BitXorInstr i: v(i.Dst); v(i.A); v(i.B); break;
            case BitNotInstr i: v(i.Dst); v(i.Src); break;
            case ShlInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case ShrInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case EqInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case NeInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case LtInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case LeInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case GtInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case GeInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case ToStrInstr i:     v(i.Dst); v(i.Src); break;
            case StrConcatInstr i: v(i.Dst); v(i.A); v(i.B); break;
            case CallInstr i:   v(i.Dst); foreach (var a in i.Args) v(a); break;
            case BuiltinInstr i: v(i.Dst); foreach (var a in i.Args) v(a); break;
            case VCallInstr i:  v(i.Dst); v(i.Obj); foreach (var a in i.Args) v(a); break;
            case FieldGetInstr i:  v(i.Dst); v(i.Obj); break;
            case FieldSetInstr i:  v(i.Obj); v(i.Val); break;
            case StaticGetInstr i: v(i.Dst); break;
            case StaticSetInstr i: v(i.Val); break;
            case ObjNewInstr i:    v(i.Dst); foreach (var a in i.Args) v(a); break;
            case IsInstanceInstr i: v(i.Dst); v(i.Obj); break;
            case AsCastInstr i:    v(i.Dst); v(i.Obj); break;
            case ArrayNewInstr i:    v(i.Dst); v(i.Size); break;
            case ArrayNewLitInstr i: v(i.Dst); foreach (var e in i.Elems) v(e); break;
            case ArrayGetInstr i:    v(i.Dst); v(i.Arr); v(i.Idx); break;
            case ArraySetInstr i:    v(i.Arr); v(i.Idx); v(i.Val); break;
            case ArrayLenInstr i:    v(i.Dst); v(i.Arr); break;
        }
    }

    private static void VisitTermRegs(IrTerminator term, Action<int> v)
    {
        switch (term)
        {
            case RetTerm { Reg: int r }: v(r); break;
            case BrCondTerm bc: v(bc.Cond); break;
            case ThrowTerm tt:  v(tt.Reg); break;
        }
    }
}

// ── String pool ───────────────────────────────────────────────────────────────

public sealed class StringPool
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string>            _list  = [];

    public int Intern(string s)
    {
        if (!_index.TryGetValue(s, out int idx))
        {
            idx = _list.Count;
            _index[s] = idx;
            _list.Add(s);
        }
        return idx;
    }

    /// Returns the index of a previously interned string; throws if not found.
    public int Idx(string s) => _index[s];

    public IReadOnlyList<string> AllStrings => _list;
    public int Count => _list.Count;
}
