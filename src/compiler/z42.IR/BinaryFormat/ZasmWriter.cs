using System.Text;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Serializes an <see cref="IrModule"/> into human-readable z42 assembly (.zasm).
///
/// The text format is a 1-to-1 projection of the binary format — every field is
/// visible, so a tool can reconstruct the binary from the text.
///
/// Example output:
///   .module "Demo.Greet"
///
///   .strings
///     s0  "Hello, "
///     s1  "world"
///
///   .func @Demo.Greet.greet  params:1  ret:str  mode:Interp
///     .block entry
///       %1:str = const.str  s0          ; "Hello, "
///       %2:str = call  @str.concat  %1, %0
///       ret  %2
/// </summary>
public static class ZasmWriter
{
    public static string Write(IrModule module)
    {
        var sb = new StringBuilder();

        // ── Module header ─────────────────────────────────────────────────────
        sb.AppendLine($".module \"{Escape(module.Name)}\"");

        // ── String pool ───────────────────────────────────────────────────────
        if (module.StringPool.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(".strings");
            for (int i = 0; i < module.StringPool.Count; i++)
                sb.AppendLine($"  s{i}  \"{Escape(module.StringPool[i])}\"");
        }

        // ── Class descriptors ─────────────────────────────────────────────────
        foreach (var cls in module.Classes)
        {
            sb.AppendLine();
            sb.Append($".class {cls.Name}");
            if (cls.BaseClass != null) sb.Append($"  .base {cls.BaseClass}");
            sb.AppendLine();
            if (cls.TypeParams is { Count: > 0 })
                sb.AppendLine($"  .type_params  {string.Join(", ", cls.TypeParams)}");
            AppendConstraintLines(sb, cls.TypeParams, cls.TypeParamConstraints);
            foreach (var fld in cls.Fields)
                sb.AppendLine($"  .field {fld.Name}: {fld.Type}");
        }

        // ── Functions ─────────────────────────────────────────────────────────
        foreach (var fn in module.Functions)
        {
            sb.AppendLine();
            // Show static keyword and add helpful param comment
            string staticSuffix = fn.IsStatic ? "  static" : "";
            string paramComment = !fn.IsStatic && fn.ParamCount > 0
                ? $"  ; %0=this" + (fn.ParamCount > 1 ? $", %1..%{fn.ParamCount - 1}=params" : "")
                : "";
            sb.AppendLine($".func @{fn.Name}  params:{fn.ParamCount}  ret:{fn.RetType}  mode:{fn.ExecMode}{staticSuffix}{paramComment}");

            // Type parameters (if present)
            if (fn.TypeParams is { Count: > 0 })
                sb.AppendLine($"  .type_params  {string.Join(", ", fn.TypeParams)}");
            AppendConstraintLines(sb, fn.TypeParams, fn.TypeParamConstraints);

            // Exception table (if present)
            if (fn.ExceptionTable is { Count: > 0 })
            {
                foreach (var exc in fn.ExceptionTable)
                {
                    string catchType = exc.CatchType ?? "*";
                    sb.AppendLine(
                        $"  .except  try:[{exc.TryStart}, {exc.TryEnd})  " +
                        $"catch:{exc.CatchLabel}  type:{catchType}  reg:{Reg(exc.CatchReg)}");
                }
            }

            // Local variable table (if present)
            if (fn.LocalVarTable is { Count: > 0 })
            {
                sb.AppendLine("  .locals");
                foreach (var lv in fn.LocalVarTable)
                    sb.AppendLine($"    %{lv.RegId} = {lv.Name}");
            }

            // Line table (if present)
            if (fn.LineTable is { Count: > 0 })
            {
                sb.AppendLine("  .linetable");
                foreach (var entry in fn.LineTable)
                    sb.AppendLine($"    block:{entry.BlockIdx}  instr:{entry.InstrIdx}  line:{entry.Line}");
            }

            // Blocks — separated by blank lines for readability
            for (int bi = 0; bi < fn.Blocks.Count; bi++)
            {
                var block = fn.Blocks[bi];
                if (bi > 0) sb.AppendLine();  // blank line between blocks
                sb.AppendLine($"  .block {block.Label}");
                foreach (var instr in block.Instructions)
                    sb.AppendLine("    " + FormatInstr(instr, module.StringPool));
                sb.AppendLine("    " + FormatTerminator(block.Terminator));
            }
        }

        return sb.ToString();
    }

    // ── Register formatting ──────────────────────────────────────────────────

    /// Format a typed register: `%3:i64` or `%3` if type is Unknown.
    private static string Reg(TypedReg r) =>
        r.Type is IrType.Unknown ? $"%{r.Id}" : $"%{r.Id}:{TypeName(r.Type)}";

    private static string TypeName(IrType t) => t switch
    {
        IrType.I8   => "i8",   IrType.I16  => "i16",
        IrType.I32  => "i32",  IrType.I64  => "i64",
        IrType.U8   => "u8",   IrType.U16  => "u16",
        IrType.U32  => "u32",  IrType.U64  => "u64",
        IrType.F32  => "f32",  IrType.F64  => "f64",
        IrType.Bool => "bool", IrType.Char => "char",
        IrType.Str  => "str",  IrType.Ref  => "ref",
        IrType.Void => "void",
        _ => "?",
    };

    // ── Instruction formatting ────────────────────────────────────────────────

    private static string FormatInstr(IrInstr instr, IReadOnlyList<string> strPool)
    {
        return instr switch
        {
            ConstStrInstr  i => $"{Reg(i.Dst)} = const.str  s{i.Idx}" +
                                 (i.Idx < strPool.Count ? $"          ; \"{Escape(strPool[i.Idx])}\"" : ""),
            ConstI32Instr  i => $"{Reg(i.Dst)} = const.i32  {i.Val}",
            ConstI64Instr  i => $"{Reg(i.Dst)} = const.i64  {i.Val}",
            ConstF64Instr  i => $"{Reg(i.Dst)} = const.f64  {i.Val}",
            ConstBoolInstr i => $"{Reg(i.Dst)} = const.bool  {(i.Val ? "true" : "false")}",
            ConstCharInstr i => $"{Reg(i.Dst)} = const.char  '{i.Val}'",
            ConstNullInstr i => $"{Reg(i.Dst)} = const.null",
            CopyInstr      i => $"{Reg(i.Dst)} = copy  {Reg(i.Src)}",

            AddInstr    i => $"{Reg(i.Dst)} = add  {Reg(i.A)}, {Reg(i.B)}",
            SubInstr    i => $"{Reg(i.Dst)} = sub  {Reg(i.A)}, {Reg(i.B)}",
            MulInstr    i => $"{Reg(i.Dst)} = mul  {Reg(i.A)}, {Reg(i.B)}",
            DivInstr    i => $"{Reg(i.Dst)} = div  {Reg(i.A)}, {Reg(i.B)}",
            RemInstr    i => $"{Reg(i.Dst)} = rem  {Reg(i.A)}, {Reg(i.B)}",
            NegInstr    i => $"{Reg(i.Dst)} = neg  {Reg(i.Src)}",
            AndInstr    i => $"{Reg(i.Dst)} = and  {Reg(i.A)}, {Reg(i.B)}",
            OrInstr     i => $"{Reg(i.Dst)} = or  {Reg(i.A)}, {Reg(i.B)}",
            NotInstr    i => $"{Reg(i.Dst)} = not  {Reg(i.Src)}",
            BitAndInstr i => $"{Reg(i.Dst)} = bit_and  {Reg(i.A)}, {Reg(i.B)}",
            BitOrInstr  i => $"{Reg(i.Dst)} = bit_or  {Reg(i.A)}, {Reg(i.B)}",
            BitXorInstr i => $"{Reg(i.Dst)} = bit_xor  {Reg(i.A)}, {Reg(i.B)}",
            BitNotInstr i => $"{Reg(i.Dst)} = bit_not  {Reg(i.Src)}",
            ShlInstr    i => $"{Reg(i.Dst)} = shl  {Reg(i.A)}, {Reg(i.B)}",
            ShrInstr    i => $"{Reg(i.Dst)} = shr  {Reg(i.A)}, {Reg(i.B)}",
            ToStrInstr  i => $"{Reg(i.Dst)} = to_str  {Reg(i.Src)}",

            EqInstr i => $"{Reg(i.Dst)} = eq  {Reg(i.A)}, {Reg(i.B)}",
            NeInstr i => $"{Reg(i.Dst)} = ne  {Reg(i.A)}, {Reg(i.B)}",
            LtInstr i => $"{Reg(i.Dst)} = lt  {Reg(i.A)}, {Reg(i.B)}",
            LeInstr i => $"{Reg(i.Dst)} = le  {Reg(i.A)}, {Reg(i.B)}",
            GtInstr i => $"{Reg(i.Dst)} = gt  {Reg(i.A)}, {Reg(i.B)}",
            GeInstr i => $"{Reg(i.Dst)} = ge  {Reg(i.A)}, {Reg(i.B)}",

            CallInstr    i => $"{Reg(i.Dst)} = call  @{i.Func}{FormatArgList(i.Args)}",
            BuiltinInstr i => $"{Reg(i.Dst)} = builtin  {i.Name}{FormatArgList(i.Args)}",
            VCallInstr   i => $"{Reg(i.Dst)} = v_call  {Reg(i.Obj)}.{i.Method}{FormatArgList(i.Args)}",

            FieldGetInstr  i => $"{Reg(i.Dst)} = field.get  {Reg(i.Obj)}  @{i.FieldName}",
            FieldSetInstr  i => $"field.set  {Reg(i.Obj)}  @{i.FieldName}  {Reg(i.Val)}",
            StaticGetInstr i => $"{Reg(i.Dst)} = static.get  @{i.Field}",
            StaticSetInstr i => $"static.set  @{i.Field}  {Reg(i.Val)}",

            ObjNewInstr     i => FormatObjNew(i),
            IsInstanceInstr i => $"{Reg(i.Dst)} = is_instance  {Reg(i.Obj)}  @{i.ClassName}",
            AsCastInstr     i => $"{Reg(i.Dst)} = as_cast  {Reg(i.Obj)}  @{i.ClassName}",

            ArrayNewInstr    i => $"{Reg(i.Dst)} = arr.new  {Reg(i.Size)}",
            ArrayNewLitInstr i => $"{Reg(i.Dst)} = arr.new.lit{FormatArgList(i.Elems)}",
            ArrayGetInstr    i => $"{Reg(i.Dst)} = arr.get  {Reg(i.Arr)}, {Reg(i.Idx)}",
            ArraySetInstr    i => $"arr.set  {Reg(i.Arr)}, {Reg(i.Idx)}, {Reg(i.Val)}",
            ArrayLenInstr    i => $"{Reg(i.Dst)} = arr.len  {Reg(i.Arr)}",
            StrConcatInstr   i => $"{Reg(i.Dst)} = str.concat  {Reg(i.A)}, {Reg(i.B)}",

            _ => $"; <unknown {instr.GetType().Name}>",
        };
    }

    private static string FormatTerminator(IrTerminator term) => term switch
    {
        RetTerm { Reg: null }      => "ret",
        RetTerm { Reg: TypedReg r } => $"ret  {Reg(r)}",
        BrTerm bt                   => $"br  {bt.Label}",
        BrCondTerm bc               => $"br.cond  {Reg(bc.Cond)}  {bc.TrueLabel}  {bc.FalseLabel}",
        ThrowTerm tt                => $"throw  {Reg(tt.Reg)}",
        _                      => $"; <unknown terminator {term.GetType().Name}>",
    };

    private static string FormatArgList(List<TypedReg> args) =>
        args.Count == 0 ? "" : "  " + string.Join(", ", args.Select(a => Reg(a)));

    /// Emit `obj.new` in compact form (single ctor) or with `ctor=` annotation
    /// (overloaded ctor, where CtorName != ClassName + "." + simple).
    private static string FormatObjNew(ObjNewInstr i)
    {
        int dot = i.ClassName.LastIndexOf('.');
        string simple = dot < 0 ? i.ClassName : i.ClassName[(dot + 1)..];
        string defaultCtor = $"{i.ClassName}.{simple}";
        string ctorAnnot = i.CtorName == defaultCtor ? "" : $"  ctor={i.CtorName}";
        return $"{Reg(i.Dst)} = obj.new  @{i.ClassName}{ctorAnnot}{FormatArgList(i.Args)}";
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    /// (L3-G3a + L3-G2.5 bare-tp) Emit `.constraint T: Base + IFoo + class` lines for non-empty bundles.
    private static void AppendConstraintLines(
        StringBuilder sb,
        IReadOnlyList<string>? typeParams,
        IReadOnlyList<IrConstraintBundle>? bundles)
    {
        if (typeParams is null || bundles is null) return;
        int n = Math.Min(typeParams.Count, bundles.Count);
        for (int i = 0; i < n; i++)
        {
            var b = bundles[i];
            if (b.IsEmpty) continue;
            var parts = new List<string>();
            if (b.BaseClass is not null) parts.Add(b.BaseClass);
            if (b.TypeParamConstraint is not null) parts.Add(b.TypeParamConstraint);
            parts.AddRange(b.Interfaces);
            if (b.RequiresClass)  parts.Add("class");
            if (b.RequiresStruct) parts.Add("struct");
            sb.AppendLine($"  .constraint  {typeParams[i]}: {string.Join(" + ", parts)}");
        }
    }
}
