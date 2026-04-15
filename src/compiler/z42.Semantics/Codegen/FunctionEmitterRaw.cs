using Z42.Core.Text;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Legacy AST expression emission — used only for:
///   • base constructor argument lists in <see cref="FunctionEmitter.EmitMethod"/>
///   • default parameter value expansion in <see cref="FunctionEmitter.FillDefaults"/>
///   • static field initializers in <see cref="FunctionEmitter.EmitStaticInit"/>
///
/// All function body emission goes through <c>EmitExpr(BoundExpr)</c> in
/// FunctionEmitterExprs.cs instead.
internal sealed partial class FunctionEmitter
{
    private int EmitRawExpr(Expr expr)
    {
        switch (expr)
        {
            case LitStrExpr s:
            {
                int dst = Alloc();
                Emit(new ConstStrInstr(dst, _gen.Intern(s.Value)));
                return dst;
            }
            case LitIntExpr n:
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case LitFloatExpr f:
            {
                int dst = Alloc();
                Emit(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case LitBoolExpr b:
            {
                int dst = Alloc();
                Emit(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case LitNullExpr:
            {
                int dst = Alloc();
                Emit(new ConstNullInstr(dst));
                return dst;
            }
            case LitCharExpr c:
            {
                int dst = Alloc();
                Emit(new ConstCharInstr(dst, c.Value));
                return dst;
            }

            case IdentExpr id:
            {
                if (_locals.TryGetValue(id.Name, out int reg))
                    return reg;
                if (_mutableVars.Contains(id.Name))
                {
                    int dst = Alloc();
                    Emit(new LoadInstr(dst, id.Name));
                    return dst;
                }
                if (_instanceFields.Contains(id.Name))
                {
                    int dst = Alloc();
                    Emit(new FieldGetInstr(dst, 0, id.Name));
                    return dst;
                }
                throw new InvalidOperationException($"undefined variable `{id.Name}` in raw emit");
            }

            case MemberExpr m when m.Target is IdentExpr enumId
                                && _gen._enumConstants.TryGetValue(
                                    $"{enumId.Name}.{m.Member}", out long enumVal):
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, enumVal));
                return dst;
            }

            case MemberExpr m when m.Target is IdentExpr { Name: var clsName }
                && _gen.TryGetStaticFieldKey(clsName, m.Member) is { } sfKey:
            {
                int dst = Alloc();
                Emit(new StaticGetInstr(dst, sfKey));
                return dst;
            }

            case MemberExpr m:
            {
                int objReg = EmitRawExpr(m.Target);
                int dst = Alloc();
                Emit(new FieldGetInstr(dst, objReg, m.Member));
                return dst;
            }

            case NewExpr when expr is NewExpr { Type: NamedType { Name: "StringBuilder" } }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__sb_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType { Name: "Dictionary" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__dict_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType { Name: "List" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__list_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType nt:
            {
                var argRegs = newExpr.Args.Select(EmitRawExpr).ToList();
                string ctorKey = $"{_gen.QualifyName(nt.Name)}.{nt.Name}";
                argRegs = FillDefaults(ctorKey, argRegs);
                int dst = Alloc();
                Emit(new ObjNewInstr(dst, _gen.QualifyName(nt.Name), argRegs));
                return dst;
            }

            case BinaryExpr bin:
            {
                int a   = EmitRawExpr(bin.Left);
                int b   = EmitRawExpr(bin.Right);
                int dst = Alloc();
                Emit(bin.Op switch
                {
                    "+"  => (IrInstr)new AddInstr(dst, a, b),
                    "-"  => new SubInstr(dst, a, b),
                    "*"  => new MulInstr(dst, a, b),
                    "/"  => new DivInstr(dst, a, b),
                    "==" => new EqInstr(dst, a, b),
                    "!=" => new NeInstr(dst, a, b),
                    "<"  => new LtInstr(dst, a, b),
                    "<=" => new LeInstr(dst, a, b),
                    ">"  => new GtInstr(dst, a, b),
                    ">=" => new GeInstr(dst, a, b),
                    "&&" => new AndInstr(dst, a, b),
                    "||" => new OrInstr(dst, a, b),
                    _    => throw new NotSupportedException($"raw binary `{bin.Op}`")
                });
                return dst;
            }

            case UnaryExpr u when u.Op is "-":
            {
                int src = EmitRawExpr(u.Operand);
                int dst = Alloc();
                Emit(new NegInstr(dst, src));
                return dst;
            }

            default:
                throw new NotSupportedException(
                    $"raw expression type {expr.GetType().Name} not supported here");
        }
    }
}
