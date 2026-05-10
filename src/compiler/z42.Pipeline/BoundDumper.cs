using System.Text;
using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Pretty-prints the post-typecheck Bound tree(s) inside a <see cref="SemanticModel"/>
/// as an indented tree, with inferred type annotations on every BoundExpr.
///
/// impl-dump-ast (2026-05-10) — built on top of the introduce-bound-visitor
/// framework: <see cref="BoundExprVisitor{TResult}"/> + <see cref="BoundStmtVisitor{TResult}"/>
/// give exhaustive dispatch with compile-time enforcement when new BoundXxx
/// nodes are added.
///
/// Format mirrors <see cref="AstDumper"/>; difference is the type annotation
/// `: &lt;Z42Type&gt;` on every BoundExpr line.
/// </summary>
public static class BoundDumper
{
    public static string Dump(SemanticModel model)
    {
        var w = new Writer();
        var stmtVisitor = new StmtDumper(w);

        // Static field initializers
        if (model.BoundStaticInits.Count > 0)
        {
            w.Line($"StaticInits [{model.BoundStaticInits.Count} items]:");
            w.Indented(() =>
            {
                foreach (var (field, expr) in model.BoundStaticInits)
                {
                    w.Line($"Field {field.Name} {FmtSpan(field.Span)}");
                    w.Indented(() => stmtVisitor.VisitExprPublic(expr));
                }
            });
        }

        // Function bodies
        if (model.BoundBodies.Count > 0)
        {
            w.Line($"Functions [{model.BoundBodies.Count} items]:");
            w.Indented(() =>
            {
                foreach (var (decl, block) in model.BoundBodies)
                {
                    w.Line($"Function {decl.Name} {FmtSpan(decl.Span)}");
                    w.Indented(() => stmtVisitor.VisitBlockPublic(block));
                }
            });
        }

        return w.Build();
    }

    private static string FmtSpan(Span s) => $"({s.Line}:{s.Column})";
    private static string FmtType(Z42Type t) => t.ToString() ?? "<null>";
    private static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

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

    /// Visitor over BoundStmt with shared writer + collaborating expr visitor.
    private sealed class StmtDumper : BoundStmtVisitor<Unit>
    {
        private readonly Writer _w;
        private readonly ExprDumper _expr;

        public StmtDumper(Writer w)
        {
            _w = w;
            _expr = new ExprDumper(w, this);
        }

        public void VisitBlockPublic(BoundBlock block)
        {
            _w.Line($"BoundBlock [{block.Stmts.Count} stmts] {FmtSpan(block.Span)}");
            _w.Indented(() =>
            {
                foreach (var s in block.Stmts) Visit(s);
            });
        }

        public void VisitExprPublic(BoundExpr e) => _expr.Visit(e);

        protected override Unit VisitBlockStmt(BoundBlockStmt b)
        {
            _w.Line($"BoundBlockStmt {FmtSpan(b.Span)}");
            _w.Indented(() => VisitBlockPublic(b.Block));
            return default;
        }

        protected override Unit VisitVarDecl(BoundVarDecl v)
        {
            _w.Line($"BoundVarDecl {v.Name} : {FmtType(v.VarType)} {FmtSpan(v.Span)}");
            if (v.Init != null) _w.Indented(() => { _w.Line("Init:"); _w.Indented(() => _expr.Visit(v.Init)); });
            return default;
        }

        protected override Unit VisitReturn(BoundReturn r)
        {
            _w.Line($"BoundReturn {FmtSpan(r.Span)}");
            if (r.Value != null) _w.Indented(() => _expr.Visit(r.Value));
            return default;
        }

        protected override Unit VisitExprStmt(BoundExprStmt e)
        {
            _w.Line($"BoundExprStmt {FmtSpan(e.Span)}");
            _w.Indented(() => _expr.Visit(e.Expr));
            return default;
        }

        protected override Unit VisitIf(BoundIf i)
        {
            _w.Line($"BoundIf {FmtSpan(i.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Cond:"); _w.Indented(() => _expr.Visit(i.Cond));
                _w.Line("Then:"); _w.Indented(() => VisitBlockPublic(i.Then));
                if (i.Else != null) { _w.Line("Else:"); _w.Indented(() => Visit(i.Else)); }
            });
            return default;
        }

        protected override Unit VisitWhile(BoundWhile w)
        {
            _w.Line($"BoundWhile {FmtSpan(w.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Cond:"); _w.Indented(() => _expr.Visit(w.Cond));
                _w.Line("Body:"); _w.Indented(() => VisitBlockPublic(w.Body));
            });
            return default;
        }

        protected override Unit VisitDoWhile(BoundDoWhile dw)
        {
            _w.Line($"BoundDoWhile {FmtSpan(dw.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Body:"); _w.Indented(() => VisitBlockPublic(dw.Body));
                _w.Line("Cond:"); _w.Indented(() => _expr.Visit(dw.Cond));
            });
            return default;
        }

        protected override Unit VisitFor(BoundFor f)
        {
            _w.Line($"BoundFor {FmtSpan(f.Span)}");
            _w.Indented(() =>
            {
                if (f.Init      != null) { _w.Line("Init:");      _w.Indented(() => Visit(f.Init)); }
                if (f.Cond      != null) { _w.Line("Cond:");      _w.Indented(() => _expr.Visit(f.Cond)); }
                if (f.Increment != null) { _w.Line("Increment:"); _w.Indented(() => _expr.Visit(f.Increment)); }
                _w.Line("Body:"); _w.Indented(() => VisitBlockPublic(f.Body));
            });
            return default;
        }

        protected override Unit VisitForeach(BoundForeach fe)
        {
            _w.Line($"BoundForeach {fe.VarName} : {FmtType(fe.VarType)} {FmtSpan(fe.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Collection:"); _w.Indented(() => _expr.Visit(fe.Collection));
                _w.Line("Body:");       _w.Indented(() => VisitBlockPublic(fe.Body));
            });
            return default;
        }

        protected override Unit VisitBreak(BoundBreak br)       { _w.Line($"BoundBreak {FmtSpan(br.Span)}"); return default; }
        protected override Unit VisitContinue(BoundContinue co) { _w.Line($"BoundContinue {FmtSpan(co.Span)}"); return default; }

        protected override Unit VisitSwitch(BoundSwitch sw)
        {
            _w.Line($"BoundSwitch {FmtSpan(sw.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Subject:"); _w.Indented(() => _expr.Visit(sw.Subject));
                foreach (var c in sw.Cases)
                {
                    if (c.Pattern == null) _w.Line($"DefaultCase {FmtSpan(c.Span)}");
                    else
                    {
                        _w.Line($"Case {FmtSpan(c.Span)}");
                        _w.Indented(() => { _w.Line("Pattern:"); _w.Indented(() => _expr.Visit(c.Pattern)); });
                    }
                    _w.Indented(() =>
                    {
                        _w.Line("Body:");
                        _w.Indented(() => { foreach (var s in c.Body) Visit(s); });
                    });
                }
            });
            return default;
        }

        protected override Unit VisitTryCatch(BoundTryCatch tc)
        {
            _w.Line($"BoundTryCatch {FmtSpan(tc.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Try:"); _w.Indented(() => VisitBlockPublic(tc.TryBody));
                foreach (var clause in tc.Catches)
                {
                    var head = $"Catch {clause.ExceptionTypeName ?? "*"}{(clause.VarName != null ? $" {clause.VarName}" : "")} {FmtSpan(clause.Span)}";
                    _w.Line(head);
                    _w.Indented(() => VisitBlockPublic(clause.Body));
                }
                if (tc.Finally != null) { _w.Line("Finally:"); _w.Indented(() => VisitBlockPublic(tc.Finally)); }
            });
            return default;
        }

        protected override Unit VisitThrow(BoundThrow th)
        {
            _w.Line($"BoundThrow {FmtSpan(th.Span)}");
            _w.Indented(() => _expr.Visit(th.Value));
            return default;
        }

        protected override Unit VisitLocalFunction(BoundLocalFunction lf)
        {
            _w.Line($"BoundLocalFunction {lf.Name} : {FmtType(lf.RetType)} {FmtSpan(lf.Span)}");
            _w.Indented(() =>
            {
                if (lf.Captures.Count > 0)
                {
                    _w.Line($"Captures [{lf.Captures.Count} items]:");
                    _w.Indented(() =>
                    {
                        foreach (var cap in lf.Captures)
                            _w.Line($"BoundCapture {cap.Name} : {FmtType(cap.Type)} ({cap.Kind}) {FmtSpan(cap.Span)}");
                    });
                }
                _w.Line("Body:"); _w.Indented(() => VisitBlockPublic(lf.Body));
            });
            return default;
        }

        protected override Unit VisitPinned(BoundPinned p)
        {
            _w.Line($"BoundPinned {p.Name} {FmtSpan(p.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Source:"); _w.Indented(() => _expr.Visit(p.Source));
                _w.Line("Body:");   _w.Indented(() => VisitBlockPublic(p.Body));
            });
            return default;
        }
    }

    /// Visitor over BoundExpr — every line carries `: <Z42Type>` annotation.
    private sealed class ExprDumper : BoundExprVisitor<Unit>
    {
        private readonly Writer _w;
        private readonly StmtDumper _stmts;

        public ExprDumper(Writer w, StmtDumper stmts) { _w = w; _stmts = stmts; }

        // ── Literals ──────────────────────────────────────────────────────────
        protected override Unit VisitLitInt(BoundLitInt n)        { _w.Line($"BoundLitInt {n.Value} : {FmtType(n.Type)} {FmtSpan(n.Span)}"); return default; }
        protected override Unit VisitLitFloat(BoundLitFloat f)    { _w.Line($"BoundLitFloat {f.Value} : {FmtType(f.Type)} {FmtSpan(f.Span)}"); return default; }
        protected override Unit VisitLitStr(BoundLitStr s)        { _w.Line($"BoundLitStr \"{Escape(s.Value)}\" : {FmtType(s.Type)} {FmtSpan(s.Span)}"); return default; }
        protected override Unit VisitLitBool(BoundLitBool b)      { _w.Line($"BoundLitBool {b.Value} : {FmtType(b.Type)} {FmtSpan(b.Span)}"); return default; }
        protected override Unit VisitLitNull(BoundLitNull n)      { _w.Line($"BoundLitNull : {FmtType(n.Type)} {FmtSpan(n.Span)}"); return default; }
        protected override Unit VisitLitChar(BoundLitChar c)      { _w.Line($"BoundLitChar '{c.Value}' : {FmtType(c.Type)} {FmtSpan(c.Span)}"); return default; }

        protected override Unit VisitDefault(BoundDefault d)
        {
            var idx = d.GenericParamIndex is int gi ? $" gpi={gi}" : "";
            _w.Line($"BoundDefault : {FmtType(d.Type)}{idx} {FmtSpan(d.Span)}");
            return default;
        }

        protected override Unit VisitInterpolatedStr(BoundInterpolatedStr i)
        {
            _w.Line($"BoundInterpolatedStr : {FmtType(i.Type)} {FmtSpan(i.Span)}");
            _w.Indented(() =>
            {
                foreach (var part in i.Parts)
                {
                    switch (part)
                    {
                        case BoundTextPart tp: _w.Line($"BoundTextPart \"{Escape(tp.Text)}\" {FmtSpan(tp.Span)}"); break;
                        case BoundExprPart ep:
                            _w.Line($"BoundExprPart {FmtSpan(ep.Span)}");
                            _w.Indented(() => Visit(ep.Inner));
                            break;
                        default: throw new NotSupportedException($"BoundDumper: unhandled BoundInterpolationPart `{part.GetType().Name}`");
                    }
                }
            });
            return default;
        }

        // ── Identifiers ───────────────────────────────────────────────────────
        protected override Unit VisitIdent(BoundIdent id)         { _w.Line($"BoundIdent {id.Name} : {FmtType(id.Type)} {FmtSpan(id.Span)}"); return default; }
        protected override Unit VisitCapturedIdent(BoundCapturedIdent ci)
        {
            _w.Line($"BoundCapturedIdent {ci.Name} : {FmtType(ci.Type)} env[{ci.CaptureIndex}] {FmtSpan(ci.Span)}");
            return default;
        }

        // ── Operators / calls ─────────────────────────────────────────────────
        protected override Unit VisitAssign(BoundAssign a)
        {
            _w.Line($"BoundAssign : {FmtType(a.Type)} {FmtSpan(a.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Target:"); _w.Indented(() => Visit(a.Target));
                _w.Line("Value:");  _w.Indented(() => Visit(a.Value));
            });
            return default;
        }

        protected override Unit VisitBinary(BoundBinary b)
        {
            _w.Line($"BoundBinary {b.Op} : {FmtType(b.Type)} {FmtSpan(b.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Left:");  _w.Indented(() => Visit(b.Left));
                _w.Line("Right:"); _w.Indented(() => Visit(b.Right));
            });
            return default;
        }

        protected override Unit VisitUnary(BoundUnary u)
        {
            _w.Line($"BoundUnary {u.Op} : {FmtType(u.Type)} {FmtSpan(u.Span)}");
            _w.Indented(() => Visit(u.Operand));
            return default;
        }

        protected override Unit VisitPostfix(BoundPostfix p)
        {
            _w.Line($"BoundPostfix {p.Op} : {FmtType(p.Type)} {FmtSpan(p.Span)}");
            _w.Indented(() => Visit(p.Operand));
            return default;
        }

        protected override Unit VisitLambda(BoundLambda l)
        {
            _w.Line($"BoundLambda : {FmtType(l.Type)} {FmtSpan(l.Span)}");
            _w.Indented(() =>
            {
                foreach (var p in l.Params)
                    _w.Line($"BoundLambdaParam {p.Name} : {FmtType(p.Type)} {FmtSpan(p.Span)}");
                if (l.Captures.Count > 0)
                {
                    _w.Line($"Captures [{l.Captures.Count} items]:");
                    _w.Indented(() =>
                    {
                        foreach (var cap in l.Captures)
                            _w.Line($"BoundCapture {cap.Name} : {FmtType(cap.Type)} ({cap.Kind}) {FmtSpan(cap.Span)}");
                    });
                }
                switch (l.Body)
                {
                    case BoundLambdaExprBody eb:
                        _w.Line($"BoundLambdaExprBody {FmtSpan(eb.Span)}");
                        _w.Indented(() => Visit(eb.Expr));
                        break;
                    case BoundLambdaBlockBody bb:
                        _w.Line($"BoundLambdaBlockBody {FmtSpan(bb.Span)}");
                        _w.Indented(() => _stmts.VisitBlockPublic(bb.Block));
                        break;
                    default: throw new NotSupportedException($"BoundDumper: unhandled BoundLambdaBody `{l.Body.GetType().Name}`");
                }
            });
            return default;
        }

        protected override Unit VisitCall(BoundCall c)
        {
            var meta = c.Kind switch
            {
                BoundCallKind.Free     => c.CalleeName ?? "?",
                BoundCallKind.Static   => $"{c.ReceiverClass}::{c.MethodName}",
                BoundCallKind.Instance => $"{c.ReceiverClass}.{c.MethodName}",
                BoundCallKind.Virtual  => $"{c.ReceiverClass}.{c.MethodName} (virtual)",
                _ => "?",
            };
            _w.Line($"BoundCall {c.Kind} {meta} : {FmtType(c.Type)} {FmtSpan(c.Span)}");
            _w.Indented(() =>
            {
                if (c.Receiver != null) { _w.Line("Receiver:"); _w.Indented(() => Visit(c.Receiver)); }
                if (c.Args.Count > 0)
                {
                    _w.Line($"Args [{c.Args.Count} items]:");
                    _w.Indented(() => { foreach (var a in c.Args) Visit(a); });
                }
            });
            return default;
        }

        protected override Unit VisitModifiedArg(BoundModifiedArg m)
        {
            _w.Line($"BoundModifiedArg {m.Modifier.ToString().ToLowerInvariant()} : {FmtType(m.Type)} {FmtSpan(m.Span)}");
            _w.Indented(() =>
            {
                if (m.OutDecl is { } od)
                    _w.Line($"BoundOutVarDecl {od.Name} : {FmtType(od.Type)} {FmtSpan(od.Span)}");
                _w.Line("Inner:"); _w.Indented(() => Visit(m.Inner));
            });
            return default;
        }

        protected override Unit VisitMember(BoundMember m)
        {
            _w.Line($"BoundMember .{m.MemberName} : {FmtType(m.Type)} {FmtSpan(m.Span)}");
            _w.Indented(() => Visit(m.Target));
            return default;
        }

        protected override Unit VisitIndex(BoundIndex ix)
        {
            _w.Line($"BoundIndex : {FmtType(ix.Type)} {FmtSpan(ix.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Target:"); _w.Indented(() => Visit(ix.Target));
                _w.Line("Index:");  _w.Indented(() => Visit(ix.Index));
            });
            return default;
        }

        protected override Unit VisitCast(BoundCast c)
        {
            _w.Line($"BoundCast : {FmtType(c.Type)} {FmtSpan(c.Span)}");
            _w.Indented(() => Visit(c.Operand));
            return default;
        }

        protected override Unit VisitNew(BoundNew n)
        {
            _w.Line($"BoundNew {n.QualName} ctor={n.CtorName} : {FmtType(n.Type)} {FmtSpan(n.Span)}");
            if (n.Args.Count > 0)
            {
                _w.Indented(() =>
                {
                    _w.Line($"Args [{n.Args.Count} items]:");
                    _w.Indented(() => { foreach (var a in n.Args) Visit(a); });
                });
            }
            return default;
        }

        protected override Unit VisitArrayCreate(BoundArrayCreate ac)
        {
            _w.Line($"BoundArrayCreate : {FmtType(ac.Type)} {FmtSpan(ac.Span)}");
            _w.Indented(() => { _w.Line("Size:"); _w.Indented(() => Visit(ac.Size)); });
            return default;
        }

        protected override Unit VisitArrayLit(BoundArrayLit al)
        {
            _w.Line($"BoundArrayLit : {FmtType(al.Type)} {FmtSpan(al.Span)}");
            if (al.Elements.Count > 0)
            {
                _w.Indented(() =>
                {
                    _w.Line($"Elements [{al.Elements.Count} items]:");
                    _w.Indented(() => { foreach (var e in al.Elements) Visit(e); });
                });
            }
            return default;
        }

        protected override Unit VisitConditional(BoundConditional t)
        {
            _w.Line($"BoundConditional : {FmtType(t.Type)} {FmtSpan(t.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Cond:"); _w.Indented(() => Visit(t.Cond));
                _w.Line("Then:"); _w.Indented(() => Visit(t.Then));
                _w.Line("Else:"); _w.Indented(() => Visit(t.Else));
            });
            return default;
        }

        protected override Unit VisitNullCoalesce(BoundNullCoalesce nc)
        {
            _w.Line($"BoundNullCoalesce : {FmtType(nc.Type)} {FmtSpan(nc.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Left:");  _w.Indented(() => Visit(nc.Left));
                _w.Line("Right:"); _w.Indented(() => Visit(nc.Right));
            });
            return default;
        }

        protected override Unit VisitNullConditional(BoundNullConditional nc)
        {
            _w.Line($"BoundNullConditional ?.{nc.MemberName} : {FmtType(nc.Type)} {FmtSpan(nc.Span)}");
            _w.Indented(() => Visit(nc.Target));
            return default;
        }

        protected override Unit VisitIsPattern(BoundIsPattern ip)
        {
            _w.Line($"BoundIsPattern is {ip.TypeName} {ip.Binding} : {FmtType(ip.Type)} {FmtSpan(ip.Span)}");
            _w.Indented(() => Visit(ip.Target));
            return default;
        }

        protected override Unit VisitSwitchExpr(BoundSwitchExpr s)
        {
            _w.Line($"BoundSwitchExpr : {FmtType(s.Type)} {FmtSpan(s.Span)}");
            _w.Indented(() =>
            {
                _w.Line("Subject:"); _w.Indented(() => Visit(s.Subject));
                foreach (var arm in s.Arms)
                {
                    _w.Line($"BoundSwitchArm {FmtSpan(arm.Span)}");
                    _w.Indented(() =>
                    {
                        if (arm.Pattern != null) { _w.Line("Pattern:"); _w.Indented(() => Visit(arm.Pattern)); }
                        else                     { _w.Line("(default)"); }
                        _w.Line("Body:"); _w.Indented(() => Visit(arm.Body));
                    });
                }
            });
            return default;
        }

        protected override Unit VisitError(BoundError err)
        {
            _w.Line($"BoundError \"{Escape(err.Message)}\" : {FmtType(err.Type)} {FmtSpan(err.Span)}");
            return default;
        }
    }
}
