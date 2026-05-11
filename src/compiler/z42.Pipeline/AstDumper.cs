using System.Text;
using Z42.Core.Text;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Pretty-prints an AST <see cref="CompilationUnit"/> as an indented tree.
///
/// impl-dump-ast (2026-05-10) — replaces the legacy `Console.WriteLine(cu)`
/// placeholder which used C# default record `ToString()` (single-line, deeply
/// nested, near-unreadable).
///
/// Format:
///   NodeKind &lt;salient-attr&gt; (line:col)
///     ChildField: ChildKind ...
///
/// AST has no inferred types — the user-written TypeExpr nodes appear in the
/// tree directly. For typed dump after TypeChecker, see <see cref="BoundDumper"/>.
///
/// 没有通用 visitor 框架（AST 没有 visitor 基类，留给独立 spec
/// `introduce-ast-visitor`），所以本类用手写 switch；新增 AST 节点时漏改
/// 会触发 default → throw NotSupportedException 而不是静默 fall-through。
/// </summary>
public static class AstDumper
{
    public static string Dump(CompilationUnit cu)
    {
        var w = new Writer();
        VisitCompilationUnit(cu, w);
        return w.Build();
    }

    // ── Top-level ─────────────────────────────────────────────────────────────

    private static void VisitCompilationUnit(CompilationUnit cu, Writer w)
    {
        w.Line($"CompilationUnit {FmtSpan(cu.Span)}");
        w.Indented(() =>
        {
            if (cu.Namespace != null) w.Line($"Namespace: {cu.Namespace}");
            VisitStringList("Usings", cu.Usings, w);
            VisitNodeList("NativeImports", cu.NativeImports, VisitNativeImport, w);
            VisitNodeList("Enums", cu.Enums, VisitEnum, w);
            VisitNodeList("Interfaces", cu.Interfaces, VisitInterface, w);
            VisitNodeList("Delegates", cu.Delegates, VisitDelegate, w);
            VisitNodeList("Classes", cu.Classes, VisitClass, w);
            VisitNodeList("Functions", cu.Functions, VisitFunction, w);
            VisitNodeList("Impls", cu.Impls, VisitImpl, w);
        });
    }

    private static void VisitNativeImport(NativeTypeImport n, Writer w)
        => w.Line($"NativeTypeImport {n.Name} from \"{n.LibName}\" {FmtSpan(n.Span)}");

    private static void VisitImpl(ImplDecl impl, Writer w)
    {
        w.Line($"ImplDecl {FmtSpan(impl.Span)}");
        w.Indented(() =>
        {
            w.Line("Trait:");
            w.Indented(() => VisitTypeExpr(impl.TraitType, w));
            w.Line("Target:");
            w.Indented(() => VisitTypeExpr(impl.TargetType, w));
            VisitNodeList("Methods", impl.Methods, VisitFunction, w);
        });
    }

    private static void VisitInterface(InterfaceDecl i, Writer w)
    {
        w.Line($"InterfaceDecl {i.Name} ({i.Visibility}) {FmtSpan(i.Span)}");
        w.Indented(() =>
        {
            VisitStringList("TypeParams", i.TypeParams, w);
            if (i.Where != null) VisitWhere(i.Where, w);
            VisitNodeList("Methods", i.Methods, VisitMethodSig, w);
        });
    }

    private static void VisitArgument(Argument a, Writer w)
    {
        var nameSuffix = a.Name is null ? "" : $" name={a.Name}";
        w.Line($"Argument{nameSuffix} {FmtSpan(a.Span)}");
        w.Indented(() => VisitExpr(a.Value, w));
    }

    private static void VisitMethodSig(MethodSignature m, Writer w)
    {
        var mods = (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "");
        w.Line($"MethodSignature {mods}{m.Name} {FmtSpan(m.Span)}");
        w.Indented(() =>
        {
            VisitNodeList("Params", m.Params, VisitParam, w);
            w.Line("ReturnType:");
            w.Indented(() => VisitTypeExpr(m.ReturnType, w));
            if (m.Body != null)
            {
                w.Line("Body:");
                w.Indented(() => VisitStmt(m.Body, w));
            }
        });
    }

    private static void VisitEnum(EnumDecl e, Writer w)
    {
        w.Line($"EnumDecl {e.Name} ({e.Visibility}) {FmtSpan(e.Span)}");
        w.Indented(() =>
        {
            foreach (var m in e.Members)
                w.Line(m.Value is long v
                    ? $"EnumMember {m.Name} = {v} {FmtSpan(m.Span)}"
                    : $"EnumMember {m.Name} {FmtSpan(m.Span)}");
        });
    }

    private static void VisitClass(ClassDecl c, Writer w)
    {
        var kind = c.IsRecord ? "record" : c.IsStruct ? "struct" : "class";
        var mods = (c.IsAbstract ? "abstract " : "") + (c.IsSealed ? "sealed " : "");
        w.Line($"ClassDecl {mods}{kind} {c.Name} ({c.Visibility}) {FmtSpan(c.Span)}");
        w.Indented(() =>
        {
            VisitStringList("TypeParams", c.TypeParams, w);
            if (c.BaseClass != null) w.Line($"BaseClass: {c.BaseClass}");
            VisitNodeList("Interfaces", c.Interfaces, VisitTypeExpr, w);
            if (c.Where != null) VisitWhere(c.Where, w);
            VisitNodeList("Fields", c.Fields, VisitField, w);
            VisitNodeList("Methods", c.Methods, VisitFunction, w);
            VisitNodeList("NestedDelegates", c.NestedDelegates, VisitDelegate, w);
        });
    }

    private static void VisitDelegate(DelegateDecl d, Writer w)
    {
        w.Line($"DelegateDecl {d.Name} ({d.Visibility}) {FmtSpan(d.Span)}");
        w.Indented(() =>
        {
            VisitStringList("TypeParams", d.TypeParams, w);
            if (d.Where != null) VisitWhere(d.Where, w);
            VisitNodeList("Params", d.Params, VisitParam, w);
            w.Line("ReturnType:");
            w.Indented(() => VisitTypeExpr(d.ReturnType, w));
        });
    }

    private static void VisitField(FieldDecl f, Writer w)
    {
        var mods = (f.IsStatic ? "static " : "") + (f.IsEvent ? "event " : "");
        w.Line($"FieldDecl {mods}{f.Name} ({f.Visibility}) {FmtSpan(f.Span)}");
        w.Indented(() =>
        {
            w.Line("Type:");
            w.Indented(() => VisitTypeExpr(f.Type, w));
            if (f.Initializer != null)
            {
                w.Line("Init:");
                w.Indented(() => VisitExpr(f.Initializer, w));
            }
        });
    }

    private static void VisitFunction(FunctionDecl f, Writer w)
    {
        var mods = FunctionModifiersString(f.Modifiers);
        w.Line($"FunctionDecl {mods}{f.Name} ({f.Visibility}) {FmtSpan(f.Span)}");
        w.Indented(() =>
        {
            VisitStringList("TypeParams", f.TypeParams, w);
            if (f.Where != null) VisitWhere(f.Where, w);
            VisitNodeList("Params", f.Params, VisitParam, w);
            w.Line("ReturnType:");
            w.Indented(() => VisitTypeExpr(f.ReturnType, w));
            if (f.NativeIntrinsic != null)
                w.Line($"NativeIntrinsic: {f.NativeIntrinsic}");
            if (f.Tier1Binding is { } tb)
                w.Line($"Tier1Binding: lib={tb.Lib} type={tb.TypeName} entry={tb.Entry}");
            VisitNodeList("BaseCtorArgs", f.BaseCtorArgs, VisitExpr, w);
            VisitNodeList("ThisCtorArgs", f.ThisCtorArgs, VisitExpr, w);
            VisitNodeList("TestAttributes", f.TestAttributes, VisitTestAttr, w);
            w.Line("Body:");
            w.Indented(() => VisitStmt(f.Body, w));
        });
    }

    private static string FunctionModifiersString(FunctionModifiers m)
    {
        if (m == FunctionModifiers.None) return "";
        var parts = new List<string>();
        if (m.HasFlag(FunctionModifiers.Static))   parts.Add("static");
        if (m.HasFlag(FunctionModifiers.Virtual))  parts.Add("virtual");
        if (m.HasFlag(FunctionModifiers.Override)) parts.Add("override");
        if (m.HasFlag(FunctionModifiers.Abstract)) parts.Add("abstract");
        if (m.HasFlag(FunctionModifiers.Extern))   parts.Add("extern");
        return string.Join(" ", parts) + " ";
    }

    private static void VisitParam(Param p, Writer w)
    {
        var mod = p.Modifier == ParamModifier.None ? "" : $"{p.Modifier.ToString().ToLowerInvariant()} ";
        w.Line($"Param {mod}{p.Name} {FmtSpan(p.Span)}");
        w.Indented(() =>
        {
            w.Line("Type:");
            w.Indented(() => VisitTypeExpr(p.Type, w));
            if (p.Default != null)
            {
                w.Line("Default:");
                w.Indented(() => VisitExpr(p.Default, w));
            }
        });
    }

    private static void VisitTestAttr(TestAttribute t, Writer w)
    {
        var typeArg = t.TypeArg != null ? $"<{t.TypeArg}>" : "";
        var named = t.NamedArgs is { Count: > 0 }
            ? "(" + string.Join(", ", t.NamedArgs.Select(kv => $"{kv.Key}: \"{kv.Value}\"")) + ")"
            : "";
        w.Line($"TestAttribute [{t.Name}{typeArg}{named}] {FmtSpan(t.Span)}");
    }

    private static void VisitWhere(WhereClause where, Writer w)
    {
        w.Line($"Where [{where.Constraints.Count} items] {FmtSpan(where.Span)}");
        w.Indented(() =>
        {
            foreach (var gc in where.Constraints)
            {
                var kinds = gc.Kinds == GenericConstraintKind.None ? "" : $" {gc.Kinds}";
                w.Line($"GenericConstraint {gc.TypeParam}{kinds} {FmtSpan(gc.Span)}");
                w.Indented(() => VisitNodeList("Constraints", gc.Constraints, VisitTypeExpr, w));
            }
        });
    }

    // ── TypeExpr ──────────────────────────────────────────────────────────────

    private static void VisitTypeExpr(TypeExpr te, Writer w)
    {
        switch (te)
        {
            case NamedType n:
                w.Line($"NamedType {n.Name} {FmtSpan(n.Span)}");
                break;
            case OptionType o:
                w.Line($"OptionType {FmtSpan(o.Span)}");
                w.Indented(() => VisitTypeExpr(o.Inner, w));
                break;
            case VoidType v:
                w.Line($"VoidType {FmtSpan(v.Span)}");
                break;
            case ArrayType a:
                w.Line($"ArrayType {FmtSpan(a.Span)}");
                w.Indented(() => VisitTypeExpr(a.Element, w));
                break;
            case GenericType g:
                w.Line($"GenericType {g.Name} {FmtSpan(g.Span)}");
                w.Indented(() => VisitNodeList("TypeArgs", g.TypeArgs, VisitTypeExpr, w));
                break;
            case MemberType mt:
                w.Line($"MemberType .{mt.Right} {FmtSpan(mt.Span)}");
                w.Indented(() =>
                {
                    w.Line("Left:");
                    w.Indented(() => VisitTypeExpr(mt.Left, w));
                });
                break;
            case FuncType ft:
                w.Line($"FuncType {FmtSpan(ft.Span)}");
                w.Indented(() =>
                {
                    VisitNodeList("ParamTypes", ft.ParamTypes, VisitTypeExpr, w);
                    w.Line("ReturnType:");
                    w.Indented(() => VisitTypeExpr(ft.ReturnType, w));
                });
                break;
            default:
                throw new NotSupportedException($"AstDumper: unhandled TypeExpr `{te.GetType().Name}` (ICE — add a case)");
        }
    }

    // ── Stmt ──────────────────────────────────────────────────────────────────

    private static void VisitStmt(Stmt s, Writer w)
    {
        switch (s)
        {
            case ErrorStmt e:
                w.Line($"ErrorStmt \"{Escape(e.Message)}\" {FmtSpan(e.Span)}");
                break;
            case VarDeclStmt v:
                w.Line($"VarDeclStmt {v.Name} {FmtSpan(v.Span)}");
                w.Indented(() =>
                {
                    if (v.TypeAnnotation != null)
                    {
                        w.Line("Type:");
                        w.Indented(() => VisitTypeExpr(v.TypeAnnotation, w));
                    }
                    if (v.Init != null)
                    {
                        w.Line("Init:");
                        w.Indented(() => VisitExpr(v.Init, w));
                    }
                });
                break;
            case ReturnStmt r:
                w.Line($"ReturnStmt {FmtSpan(r.Span)}");
                if (r.Value != null) w.Indented(() => VisitExpr(r.Value, w));
                break;
            case ExprStmt es:
                w.Line($"ExprStmt {FmtSpan(es.Span)}");
                w.Indented(() => VisitExpr(es.Expr, w));
                break;
            case BlockStmt b:
                w.Line($"BlockStmt {FmtSpan(b.Span)}");
                w.Indented(() =>
                {
                    foreach (var st in b.Stmts) VisitStmt(st, w);
                });
                break;
            case IfStmt ifs:
                w.Line($"IfStmt {FmtSpan(ifs.Span)}");
                w.Indented(() =>
                {
                    w.Line("Cond:");
                    w.Indented(() => VisitExpr(ifs.Condition, w));
                    w.Line("Then:");
                    w.Indented(() => VisitStmt(ifs.Then, w));
                    if (ifs.Else != null)
                    {
                        w.Line("Else:");
                        w.Indented(() => VisitStmt(ifs.Else, w));
                    }
                });
                break;
            case WhileStmt ws:
                w.Line($"WhileStmt {FmtSpan(ws.Span)}");
                w.Indented(() =>
                {
                    w.Line("Cond:");
                    w.Indented(() => VisitExpr(ws.Condition, w));
                    w.Line("Body:");
                    w.Indented(() => VisitStmt(ws.Body, w));
                });
                break;
            case DoWhileStmt dw:
                w.Line($"DoWhileStmt {FmtSpan(dw.Span)}");
                w.Indented(() =>
                {
                    w.Line("Body:");
                    w.Indented(() => VisitStmt(dw.Body, w));
                    w.Line("Cond:");
                    w.Indented(() => VisitExpr(dw.Condition, w));
                });
                break;
            case ForStmt fs:
                w.Line($"ForStmt {FmtSpan(fs.Span)}");
                w.Indented(() =>
                {
                    if (fs.Init != null)      { w.Line("Init:");      w.Indented(() => VisitStmt(fs.Init, w)); }
                    if (fs.Condition != null) { w.Line("Cond:");      w.Indented(() => VisitExpr(fs.Condition, w)); }
                    if (fs.Increment != null) { w.Line("Increment:"); w.Indented(() => VisitExpr(fs.Increment, w)); }
                    w.Line("Body:");
                    w.Indented(() => VisitStmt(fs.Body, w));
                });
                break;
            case ForeachStmt fe:
                w.Line($"ForeachStmt {fe.VarName} {FmtSpan(fe.Span)}");
                w.Indented(() =>
                {
                    w.Line("Collection:");
                    w.Indented(() => VisitExpr(fe.Collection, w));
                    w.Line("Body:");
                    w.Indented(() => VisitStmt(fe.Body, w));
                });
                break;
            case BreakStmt br:
                w.Line($"BreakStmt {FmtSpan(br.Span)}");
                break;
            case ContinueStmt co:
                w.Line($"ContinueStmt {FmtSpan(co.Span)}");
                break;
            case SwitchStmt sw:
                w.Line($"SwitchStmt {FmtSpan(sw.Span)}");
                w.Indented(() =>
                {
                    w.Line("Subject:");
                    w.Indented(() => VisitExpr(sw.Subject, w));
                    foreach (var c in sw.Cases)
                    {
                        if (c.Pattern == null) w.Line($"DefaultCase {FmtSpan(c.Span)}");
                        else
                        {
                            w.Line($"Case {FmtSpan(c.Span)}");
                            w.Indented(() =>
                            {
                                w.Line("Pattern:");
                                w.Indented(() => VisitExpr(c.Pattern, w));
                            });
                        }
                        w.Indented(() =>
                        {
                            w.Line("Body:");
                            w.Indented(() => { foreach (var st in c.Body) VisitStmt(st, w); });
                        });
                    }
                });
                break;
            case TryCatchStmt tc:
                w.Line($"TryCatchStmt {FmtSpan(tc.Span)}");
                w.Indented(() =>
                {
                    w.Line("Try:");
                    w.Indented(() => VisitStmt(tc.TryBody, w));
                    foreach (var clause in tc.Catches)
                    {
                        var head = $"Catch {clause.ExceptionType ?? "*"}{(clause.VarName != null ? $" {clause.VarName}" : "")} {FmtSpan(clause.Span)}";
                        w.Line(head);
                        w.Indented(() => VisitStmt(clause.Body, w));
                    }
                    if (tc.Finally != null)
                    {
                        w.Line("Finally:");
                        w.Indented(() => VisitStmt(tc.Finally, w));
                    }
                });
                break;
            case ThrowStmt th:
                w.Line($"ThrowStmt {FmtSpan(th.Span)}");
                if (th.Value != null) w.Indented(() => VisitExpr(th.Value, w));
                break;
            case LocalFunctionStmt lf:
                w.Line($"LocalFunctionStmt {FmtSpan(lf.Span)}");
                w.Indented(() => VisitFunction(lf.Decl, w));
                break;
            case PinnedStmt p:
                w.Line($"PinnedStmt {p.Name} {FmtSpan(p.Span)}");
                w.Indented(() =>
                {
                    w.Line("Source:");
                    w.Indented(() => VisitExpr(p.Source, w));
                    w.Line("Body:");
                    w.Indented(() => VisitStmt(p.Body, w));
                });
                break;
            default:
                throw new NotSupportedException($"AstDumper: unhandled Stmt `{s.GetType().Name}` (ICE — add a case)");
        }
    }

    // ── Expr ──────────────────────────────────────────────────────────────────

    private static void VisitExpr(Expr e, Writer w)
    {
        switch (e)
        {
            case ErrorExpr err:
                w.Line($"ErrorExpr \"{Escape(err.Message)}\" {FmtSpan(err.Span)}");
                break;
            case LitIntExpr n:    w.Line($"LitIntExpr {n.Value} {FmtSpan(n.Span)}"); break;
            case LitFloatExpr f:  w.Line($"LitFloatExpr {f.Value}{(f.IsFloat ? "f" : "")} {FmtSpan(f.Span)}"); break;
            case LitStrExpr s:    w.Line($"LitStrExpr \"{Escape(s.Value)}\" {FmtSpan(s.Span)}"); break;
            case LitBoolExpr b:   w.Line($"LitBoolExpr {b.Value} {FmtSpan(b.Span)}"); break;
            case LitNullExpr nu:  w.Line($"LitNullExpr {FmtSpan(nu.Span)}"); break;
            case LitCharExpr c:   w.Line($"LitCharExpr '{c.Value}' {FmtSpan(c.Span)}"); break;
            case DefaultExpr d:
                w.Line($"DefaultExpr {FmtSpan(d.Span)}");
                w.Indented(() =>
                {
                    w.Line("Target:");
                    w.Indented(() => VisitTypeExpr(d.Target, w));
                });
                break;
            case InterpolatedStrExpr i:
                w.Line($"InterpolatedStrExpr {FmtSpan(i.Span)}");
                w.Indented(() =>
                {
                    foreach (var part in i.Parts)
                    {
                        switch (part)
                        {
                            case TextPart tp: w.Line($"TextPart \"{Escape(tp.Text)}\" {FmtSpan(tp.Span)}"); break;
                            case ExprPart ep:
                                w.Line($"ExprPart {FmtSpan(ep.Span)}");
                                w.Indented(() => VisitExpr(ep.Inner, w));
                                break;
                            default: throw new NotSupportedException($"AstDumper: unhandled InterpolationPart `{part.GetType().Name}`");
                        }
                    }
                });
                break;
            case IdentExpr id:    w.Line($"IdentExpr {id.Name} {FmtSpan(id.Span)}"); break;
            case BinaryExpr bin:
                w.Line($"BinaryExpr {bin.Op} {FmtSpan(bin.Span)}");
                w.Indented(() =>
                {
                    w.Line("Left:");  w.Indented(() => VisitExpr(bin.Left, w));
                    w.Line("Right:"); w.Indented(() => VisitExpr(bin.Right, w));
                });
                break;
            case UnaryExpr u:
                w.Line($"UnaryExpr {u.Op} {FmtSpan(u.Span)}");
                w.Indented(() => VisitExpr(u.Operand, w));
                break;
            case PostfixExpr p:
                w.Line($"PostfixExpr {p.Op} {FmtSpan(p.Span)}");
                w.Indented(() => VisitExpr(p.Operand, w));
                break;
            case AssignExpr a:
                w.Line($"AssignExpr {FmtSpan(a.Span)}");
                w.Indented(() =>
                {
                    w.Line("Target:"); w.Indented(() => VisitExpr(a.Target, w));
                    w.Line("Value:");  w.Indented(() => VisitExpr(a.Value, w));
                });
                break;
            case CallExpr c:
                w.Line($"CallExpr {FmtSpan(c.Span)}");
                w.Indented(() =>
                {
                    w.Line("Callee:"); w.Indented(() => VisitExpr(c.Callee, w));
                    VisitNodeList("Args", c.Args, VisitArgument, w);
                });
                break;
            case ModifiedArg m:
                w.Line($"ModifiedArg {m.Modifier.ToString().ToLowerInvariant()} {FmtSpan(m.Span)}");
                w.Indented(() =>
                {
                    if (m.OutDecl is { } od)
                    {
                        var anno = od.AnnotatedType != null ? "" : " var";
                        w.Line($"OutVarDecl{anno} {od.Name} {FmtSpan(od.Span)}");
                        if (od.AnnotatedType != null)
                            w.Indented(() => VisitTypeExpr(od.AnnotatedType, w));
                    }
                    w.Line("Inner:");
                    w.Indented(() => VisitExpr(m.Inner, w));
                });
                break;
            case MemberExpr me:
                w.Line($"MemberExpr .{me.Member} {FmtSpan(me.Span)}");
                w.Indented(() => VisitExpr(me.Target, w));
                break;
            case IndexExpr ix:
                w.Line($"IndexExpr {FmtSpan(ix.Span)}");
                w.Indented(() =>
                {
                    w.Line("Target:"); w.Indented(() => VisitExpr(ix.Target, w));
                    w.Line("Index:");  w.Indented(() => VisitExpr(ix.Index, w));
                });
                break;
            case ConditionalExpr ce:
                w.Line($"ConditionalExpr {FmtSpan(ce.Span)}");
                w.Indented(() =>
                {
                    w.Line("Cond:"); w.Indented(() => VisitExpr(ce.Cond, w));
                    w.Line("Then:"); w.Indented(() => VisitExpr(ce.Then, w));
                    w.Line("Else:"); w.Indented(() => VisitExpr(ce.Else, w));
                });
                break;
            case NullCoalesceExpr nc:
                w.Line($"NullCoalesceExpr {FmtSpan(nc.Span)}");
                w.Indented(() =>
                {
                    w.Line("Left:");  w.Indented(() => VisitExpr(nc.Left, w));
                    w.Line("Right:"); w.Indented(() => VisitExpr(nc.Right, w));
                });
                break;
            case CastExpr cs:
                w.Line($"CastExpr {FmtSpan(cs.Span)}");
                w.Indented(() =>
                {
                    w.Line("Target:");  w.Indented(() => VisitTypeExpr(cs.TargetType, w));
                    w.Line("Operand:"); w.Indented(() => VisitExpr(cs.Operand, w));
                });
                break;
            case NewExpr nw:
                w.Line($"NewExpr {FmtSpan(nw.Span)}");
                w.Indented(() =>
                {
                    w.Line("Type:"); w.Indented(() => VisitTypeExpr(nw.Type, w));
                    VisitNodeList("Args", nw.Args, VisitArgument, w);
                });
                break;
            case ArrayCreateExpr ac:
                w.Line($"ArrayCreateExpr {FmtSpan(ac.Span)}");
                w.Indented(() =>
                {
                    w.Line("ElemType:"); w.Indented(() => VisitTypeExpr(ac.ElemType, w));
                    w.Line("Size:");     w.Indented(() => VisitExpr(ac.Size, w));
                });
                break;
            case ArrayLitExpr al:
                w.Line($"ArrayLitExpr {FmtSpan(al.Span)}");
                w.Indented(() =>
                {
                    w.Line("ElemType:"); w.Indented(() => VisitTypeExpr(al.ElemType, w));
                    VisitNodeList("Elements", al.Elements, VisitExpr, w);
                });
                break;
            case LambdaExpr lam:
                w.Line($"LambdaExpr {FmtSpan(lam.Span)}");
                w.Indented(() =>
                {
                    foreach (var p in lam.Params)
                    {
                        w.Line($"LambdaParam {p.Name} {FmtSpan(p.Span)}");
                        if (p.Type != null)
                            w.Indented(() => VisitTypeExpr(p.Type, w));
                    }
                    switch (lam.Body)
                    {
                        case LambdaExprBody eb:
                            w.Line($"LambdaExprBody {FmtSpan(eb.Span)}");
                            w.Indented(() => VisitExpr(eb.Expr, w));
                            break;
                        case LambdaBlockBody bb:
                            w.Line($"LambdaBlockBody {FmtSpan(bb.Span)}");
                            w.Indented(() => VisitStmt(bb.Block, w));
                            break;
                        default: throw new NotSupportedException($"AstDumper: unhandled LambdaBody `{lam.Body.GetType().Name}`");
                    }
                });
                break;
            case SwitchExpr sw:
                w.Line($"SwitchExpr {FmtSpan(sw.Span)}");
                w.Indented(() =>
                {
                    w.Line("Subject:");
                    w.Indented(() => VisitExpr(sw.Subject, w));
                    foreach (var arm in sw.Arms)
                    {
                        w.Line($"SwitchArm {FmtSpan(arm.Span)}");
                        w.Indented(() =>
                        {
                            if (arm.Pattern != null)
                            {
                                w.Line("Pattern:");
                                w.Indented(() => VisitExpr(arm.Pattern, w));
                            }
                            else
                            {
                                w.Line("(default)");
                            }
                            w.Line("Body:");
                            w.Indented(() => VisitExpr(arm.Body, w));
                        });
                    }
                });
                break;
            case NullConditionalExpr nce:
                w.Line($"NullConditionalExpr ?.{nce.Member} {FmtSpan(nce.Span)}");
                w.Indented(() => VisitExpr(nce.Target, w));
                break;
            case IsPatternExpr ip:
                w.Line($"IsPatternExpr is {ip.TypeName} {ip.Binding} {FmtSpan(ip.Span)}");
                w.Indented(() => VisitExpr(ip.Target, w));
                break;
            default:
                throw new NotSupportedException($"AstDumper: unhandled Expr `{e.GetType().Name}` (ICE — add a case)");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void VisitNodeList<T>(string label, IReadOnlyList<T>? list, Action<T, Writer> visit, Writer w)
    {
        if (list is null || list.Count == 0) return;
        w.Line($"{label} [{list.Count} items]:");
        w.Indented(() => { foreach (var item in list) visit(item, w); });
    }

    private static void VisitStringList(string label, IReadOnlyList<string>? list, Writer w)
    {
        if (list is null || list.Count == 0) return;
        w.Line($"{label} [{list.Count} items]: {string.Join(", ", list)}");
    }

    private static string FmtSpan(Span s) => $"({s.Line}:{s.Column})";

    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    /// Tree builder helper: indent-aware string buffer.
    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private int _indent;

        public void Line(string s)
        {
            _sb.Append(' ', _indent * 2);
            _sb.AppendLine(s);
        }

        public void Indented(Action body)
        {
            _indent++;
            try { body(); }
            finally { _indent--; }
        }

        public string Build() => _sb.ToString();
    }
}
