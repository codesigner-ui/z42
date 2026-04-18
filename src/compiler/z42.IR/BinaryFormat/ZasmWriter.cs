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
///       %1 = const.str  s0          ; "Hello, "
///       %2 = call  @str.concat  %1, %0
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

            // Exception table (if present)
            if (fn.ExceptionTable is { Count: > 0 })
            {
                foreach (var exc in fn.ExceptionTable)
                {
                    string catchType = exc.CatchType ?? "*";
                    sb.AppendLine(
                        $"  .except  try:[{exc.TryStart}, {exc.TryEnd})  " +
                        $"catch:{exc.CatchLabel}  type:{catchType}  reg:%{exc.CatchReg.Id}");
                }
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

    // ── Instruction formatting ────────────────────────────────────────────────

    private static string FormatInstr(IrInstr instr, IReadOnlyList<string> strPool)
    {
        return instr switch
        {
            ConstStrInstr  i => $"%{i.Dst.Id} = const.str  s{i.Idx}" +
                                 (i.Idx < strPool.Count ? $"          ; \"{Escape(strPool[i.Idx])}\"" : ""),
            ConstI32Instr  i => $"%{i.Dst.Id} = const.i32  {i.Val}",
            ConstI64Instr  i => $"%{i.Dst.Id} = const.i64  {i.Val}",
            ConstF64Instr  i => $"%{i.Dst.Id} = const.f64  {i.Val}",
            ConstBoolInstr i => $"%{i.Dst.Id} = const.bool  {(i.Val ? "true" : "false")}",
            ConstCharInstr i => $"%{i.Dst.Id} = const.char  '{i.Val}'",
            ConstNullInstr i => $"%{i.Dst.Id} = const.null",
            CopyInstr      i => $"%{i.Dst.Id} = copy  %{i.Src.Id}",

            AddInstr    i => $"%{i.Dst.Id} = add  %{i.A.Id}, %{i.B.Id}",
            SubInstr    i => $"%{i.Dst.Id} = sub  %{i.A.Id}, %{i.B.Id}",
            MulInstr    i => $"%{i.Dst.Id} = mul  %{i.A.Id}, %{i.B.Id}",
            DivInstr    i => $"%{i.Dst.Id} = div  %{i.A.Id}, %{i.B.Id}",
            RemInstr    i => $"%{i.Dst.Id} = rem  %{i.A.Id}, %{i.B.Id}",
            NegInstr    i => $"%{i.Dst.Id} = neg  %{i.Src.Id}",
            AndInstr    i => $"%{i.Dst.Id} = and  %{i.A.Id}, %{i.B.Id}",
            OrInstr     i => $"%{i.Dst.Id} = or  %{i.A.Id}, %{i.B.Id}",
            NotInstr    i => $"%{i.Dst.Id} = not  %{i.Src.Id}",
            BitAndInstr i => $"%{i.Dst.Id} = bit_and  %{i.A.Id}, %{i.B.Id}",
            BitOrInstr  i => $"%{i.Dst.Id} = bit_or  %{i.A.Id}, %{i.B.Id}",
            BitXorInstr i => $"%{i.Dst.Id} = bit_xor  %{i.A.Id}, %{i.B.Id}",
            BitNotInstr i => $"%{i.Dst.Id} = bit_not  %{i.Src.Id}",
            ShlInstr    i => $"%{i.Dst.Id} = shl  %{i.A.Id}, %{i.B.Id}",
            ShrInstr    i => $"%{i.Dst.Id} = shr  %{i.A.Id}, %{i.B.Id}",
            ToStrInstr  i => $"%{i.Dst.Id} = to_str  %{i.Src.Id}",

            EqInstr i => $"%{i.Dst.Id} = eq  %{i.A.Id}, %{i.B.Id}",
            NeInstr i => $"%{i.Dst.Id} = ne  %{i.A.Id}, %{i.B.Id}",
            LtInstr i => $"%{i.Dst.Id} = lt  %{i.A.Id}, %{i.B.Id}",
            LeInstr i => $"%{i.Dst.Id} = le  %{i.A.Id}, %{i.B.Id}",
            GtInstr i => $"%{i.Dst.Id} = gt  %{i.A.Id}, %{i.B.Id}",
            GeInstr i => $"%{i.Dst.Id} = ge  %{i.A.Id}, %{i.B.Id}",

            CallInstr    i => $"%{i.Dst.Id} = call  @{i.Func}{FormatArgList(i.Args)}",
            BuiltinInstr i => $"%{i.Dst.Id} = builtin  {i.Name}{FormatArgList(i.Args)}",
            // Virtual call: %dst = v_call %obj.Method  args — reads like an instance method call
            VCallInstr   i => $"%{i.Dst.Id} = v_call  %{i.Obj.Id}.{i.Method}{FormatArgList(i.Args)}",

            FieldGetInstr  i => $"%{i.Dst.Id} = field.get  %{i.Obj.Id}  @{i.FieldName}",
            FieldSetInstr  i => $"field.set  %{i.Obj.Id}  @{i.FieldName}  %{i.Val.Id}",
            StaticGetInstr i => $"%{i.Dst.Id} = static.get  @{i.Field}",
            StaticSetInstr i => $"static.set  @{i.Field}  %{i.Val.Id}",

            ObjNewInstr     i => $"%{i.Dst.Id} = obj.new  @{i.ClassName}{FormatArgList(i.Args)}",
            IsInstanceInstr i => $"%{i.Dst.Id} = is_instance  %{i.Obj.Id}  @{i.ClassName}",
            AsCastInstr     i => $"%{i.Dst.Id} = as_cast  %{i.Obj.Id}  @{i.ClassName}",

            ArrayNewInstr    i => $"%{i.Dst.Id} = arr.new  %{i.Size.Id}",
            ArrayNewLitInstr i => $"%{i.Dst.Id} = arr.new.lit{FormatArgList(i.Elems)}",
            ArrayGetInstr    i => $"%{i.Dst.Id} = arr.get  %{i.Arr.Id}, %{i.Idx.Id}",
            ArraySetInstr    i => $"arr.set  %{i.Arr.Id}, %{i.Idx.Id}, %{i.Val.Id}",
            ArrayLenInstr    i => $"%{i.Dst.Id} = arr.len  %{i.Arr.Id}",
            StrConcatInstr   i => $"%{i.Dst.Id} = str.concat  %{i.A.Id}, %{i.B.Id}",

            _ => $"; <unknown {instr.GetType().Name}>",
        };
    }

    private static string FormatTerminator(IrTerminator term) => term switch
    {
        RetTerm { Reg: null }      => "ret",
        RetTerm { Reg: TypedReg r } => $"ret  %{r.Id}",
        BrTerm bt                   => $"br  {bt.Label}",
        BrCondTerm bc               => $"br.cond  %{bc.Cond.Id}  {bc.TrueLabel}  {bc.FalseLabel}",
        ThrowTerm tt                => $"throw  %{tt.Reg.Id}",
        _                      => $"; <unknown terminator {term.GetType().Name}>",
    };

    private static string FormatArgList(List<TypedReg> args) =>
        args.Count == 0 ? "" : "  " + string.Join(", ", args.Select(a => $"%{a.Id}"));

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
