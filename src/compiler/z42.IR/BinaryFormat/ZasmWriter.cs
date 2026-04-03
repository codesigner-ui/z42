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
///     .regs 3
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
            sb.AppendLine($".func @{fn.Name}  params:{fn.ParamCount}  ret:{fn.RetType}  mode:{fn.ExecMode}");

            // Exception table (if present)
            if (fn.ExceptionTable is { Count: > 0 })
            {
                foreach (var exc in fn.ExceptionTable)
                {
                    string catchType = exc.CatchType ?? "*";
                    sb.AppendLine(
                        $"  .except  try:[{exc.TryStart}, {exc.TryEnd})  " +
                        $"catch:{exc.CatchLabel}  type:{catchType}  reg:%{exc.CatchReg}");
                }
            }

            // Blocks
            foreach (var block in fn.Blocks)
            {
                sb.AppendLine($"  .block {block.Label}");
                foreach (var instr in block.Instructions)
                    sb.AppendLine("    " + FormatInstr(instr, module.StringPool));
                sb.AppendLine("    " + FormatTerminator(block.Terminator));
            }
        }

        return sb.ToString();
    }

    // ── Instruction formatting ────────────────────────────────────────────────

    private static string FormatInstr(IrInstr instr, List<string> strPool)
    {
        return instr switch
        {
            ConstStrInstr  i => $"%{i.Dst} = const.str  s{i.Idx}" +
                                 (i.Idx < strPool.Count ? $"          ; \"{Escape(strPool[i.Idx])}\"" : ""),
            ConstI32Instr  i => $"%{i.Dst} = const.i32  {i.Val}",
            ConstI64Instr  i => $"%{i.Dst} = const.i64  {i.Val}",
            ConstF64Instr  i => $"%{i.Dst} = const.f64  {i.Val}",
            ConstBoolInstr i => $"%{i.Dst} = const.bool  {(i.Val ? "true" : "false")}",
            ConstNullInstr i => $"%{i.Dst} = const.null",
            CopyInstr      i => $"%{i.Dst} = copy  %{i.Src}",
            StoreInstr     i => $"store  @{i.Var}  %{i.Src}",
            LoadInstr      i => $"%{i.Dst} = load  @{i.Var}",

            AddInstr    i => $"%{i.Dst} = add  %{i.A}, %{i.B}",
            SubInstr    i => $"%{i.Dst} = sub  %{i.A}, %{i.B}",
            MulInstr    i => $"%{i.Dst} = mul  %{i.A}, %{i.B}",
            DivInstr    i => $"%{i.Dst} = div  %{i.A}, %{i.B}",
            RemInstr    i => $"%{i.Dst} = rem  %{i.A}, %{i.B}",
            NegInstr    i => $"%{i.Dst} = neg  %{i.Src}",
            AndInstr    i => $"%{i.Dst} = and  %{i.A}, %{i.B}",
            OrInstr     i => $"%{i.Dst} = or  %{i.A}, %{i.B}",
            NotInstr    i => $"%{i.Dst} = not  %{i.Src}",
            BitAndInstr i => $"%{i.Dst} = bit_and  %{i.A}, %{i.B}",
            BitOrInstr  i => $"%{i.Dst} = bit_or  %{i.A}, %{i.B}",
            BitXorInstr i => $"%{i.Dst} = bit_xor  %{i.A}, %{i.B}",
            BitNotInstr i => $"%{i.Dst} = bit_not  %{i.Src}",
            ShlInstr    i => $"%{i.Dst} = shl  %{i.A}, %{i.B}",
            ShrInstr    i => $"%{i.Dst} = shr  %{i.A}, %{i.B}",
            ToStrInstr  i => $"%{i.Dst} = to_str  %{i.Src}",

            EqInstr i => $"%{i.Dst} = eq  %{i.A}, %{i.B}",
            NeInstr i => $"%{i.Dst} = ne  %{i.A}, %{i.B}",
            LtInstr i => $"%{i.Dst} = lt  %{i.A}, %{i.B}",
            LeInstr i => $"%{i.Dst} = le  %{i.A}, %{i.B}",
            GtInstr i => $"%{i.Dst} = gt  %{i.A}, %{i.B}",
            GeInstr i => $"%{i.Dst} = ge  %{i.A}, %{i.B}",

            CallInstr    i => $"%{i.Dst} = call  @{i.Func}{FormatArgList(i.Args)}",
            BuiltinInstr i => $"%{i.Dst} = builtin  {i.Name}{FormatArgList(i.Args)}",
            VCallInstr   i => $"%{i.Dst} = v_call  @{i.Method}  %{i.Obj}{FormatArgList(i.Args)}",

            FieldGetInstr  i => $"%{i.Dst} = field.get  %{i.Obj}  @{i.FieldName}",
            FieldSetInstr  i => $"field.set  %{i.Obj}  @{i.FieldName}  %{i.Val}",
            StaticGetInstr i => $"%{i.Dst} = static.get  @{i.Field}",
            StaticSetInstr i => $"static.set  @{i.Field}  %{i.Val}",

            ObjNewInstr     i => $"%{i.Dst} = obj.new  @{i.ClassName}{FormatArgList(i.Args)}",
            IsInstanceInstr i => $"%{i.Dst} = is_instance  %{i.Obj}  @{i.ClassName}",
            AsCastInstr     i => $"%{i.Dst} = as_cast  %{i.Obj}  @{i.ClassName}",

            ArrayNewInstr    i => $"%{i.Dst} = arr.new  %{i.Size}",
            ArrayNewLitInstr i => $"%{i.Dst} = arr.new.lit{FormatArgList(i.Elems)}",
            ArrayGetInstr    i => $"%{i.Dst} = arr.get  %{i.Arr}, %{i.Idx}",
            ArraySetInstr    i => $"arr.set  %{i.Arr}, %{i.Idx}, %{i.Val}",
            ArrayLenInstr    i => $"%{i.Dst} = arr.len  %{i.Arr}",
            StrConcatInstr   i => $"%{i.Dst} = str.concat  %{i.A}, %{i.B}",

            _ => $"; <unknown {instr.GetType().Name}>",
        };
    }

    private static string FormatTerminator(IrTerminator term) => term switch
    {
        RetTerm { Reg: null }  => "ret",
        RetTerm { Reg: int r } => $"ret  %{r}",
        BrTerm bt              => $"br  {bt.Label}",
        BrCondTerm bc          => $"br.cond  %{bc.Cond}  {bc.TrueLabel}  {bc.FalseLabel}",
        ThrowTerm tt           => $"throw  %{tt.Reg}",
        _                      => $"; <unknown terminator {term.GetType().Name}>",
    };

    private static string FormatArgList(List<int> args) =>
        args.Count == 0 ? "" : "  " + string.Join(", ", args.Select(a => $"%{a}"));

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
