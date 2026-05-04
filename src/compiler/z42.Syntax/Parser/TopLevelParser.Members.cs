using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// 类型成员的特殊 parsing：indexer (L3-G4e) + auto-property（class body 与
/// interface body 两种 desugar 形式）。与 TopLevelParser.Helpers.cs 的通用
/// 工具方法分离。
internal static partial class TopLevelParser
{
    // ── L3-G4e: indexer ───────────────────────────────────────────────────────

    /// Detects `<vis>? <type> this [` in a class body — the indexer signature.
    /// Lookahead only; does not consume.
    private static bool IsIndexerDecl(TokenCursor cursor)
    {
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public    or TokenKind.Private or TokenKind.Protected
            or TokenKind.Internal or TokenKind.Static or TokenKind.Sealed);
        if (!TypeParser.IsTypeToken(cursor.Current.Kind)) return false;
        cursor = cursor.Advance();
        // Optional [] for array element type
        if (cursor.Current.Kind == TokenKind.LBracket
            && cursor.Peek(1).Kind == TokenKind.RBracket)
            cursor = cursor.Advance(2);
        // Optional <TypeArgs> (for generic return type like `T`) — GtGt 计为 2 个 close
        if (cursor.Current.Kind == TokenKind.Lt)
        {
            int depth = 1; cursor = cursor.Advance();
            while (!cursor.IsEnd && depth > 0)
            {
                if (cursor.Current.Kind == TokenKind.Lt) depth++;
                else if (cursor.Current.Kind == TokenKind.Gt) depth--;
                else if (cursor.Current.Kind == TokenKind.GtGt) depth -= 2;
                cursor = cursor.Advance();
            }
        }
        // Optional ? for nullable
        if (cursor.Current.Kind == TokenKind.Question) cursor = cursor.Advance();
        // Must be `this [`
        return cursor.Current is { Kind: TokenKind.Identifier, Text: "this" }
            && cursor.Peek(1).Kind == TokenKind.LBracket;
    }

    /// Parse an indexer declaration and desugar into two synthetic FunctionDecls:
    /// one for `get` (named `get_Item`) and one for `set` (named `set_Item`, with
    /// implicit `value` parameter of the indexer's element type).
    /// Either accessor may be absent; at least one must be present.
    private static IEnumerable<FunctionDecl> ParseIndexerDecl(
        ref TokenCursor cursor, LanguageFeatures feat, DiagnosticBag? diags)
    {
        var start   = cursor.Current.Span;
        var vis     = ParseVisibility(ref cursor, Visibility.Internal);
        ParseNonVisibilityModifiers(ref cursor); // no static-indexer in L3-G4e; ignored
        var elemType = TypeParser.Parse(cursor).Unwrap(ref cursor);
        // Consume `this`
        var thisTok = ExpectKind(ref cursor, TokenKind.Identifier);
        if (thisTok.Text != "this")
            throw new ParseException(
                $"expected `this` in indexer declaration, got `{thisTok.Text}`",
                thisTok.Span, DiagnosticCodes.UnexpectedToken);
        // Params: [int i] or [int row, int col]
        ExpectKind(ref cursor, TokenKind.LBracket);
        var indexParams = new List<Param>();
        while (cursor.Current.Kind != TokenKind.RBracket && !cursor.IsEnd)
        {
            var pSpan = cursor.Current.Span;
            var pType = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var pName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            indexParams.Add(new Param(pName, pType, null, pSpan));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.RBracket);
        // Body: { get { ... } [set { ... }] } — order-insensitive, at least one required.
        ExpectKind(ref cursor, TokenKind.LBrace);
        BlockStmt? getBody = null;
        BlockStmt? setBody = null;
        Span       getSpan = start, setSpan = start;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            ParseVisibility(ref cursor, vis); // accessor visibility ignored in L3-G4e
            var kw = cursor.Current;
            if (kw.Kind != TokenKind.Identifier || (kw.Text != "get" && kw.Text != "set"))
                throw new ParseException(
                    $"expected `get` or `set` in indexer body, got `{kw.Text}`",
                    kw.Span, DiagnosticCodes.UnexpectedToken);
            cursor = cursor.Advance();
            var body = StmtParser.ParseBlock(cursor, feat, diags).Unwrap(ref cursor);
            if (kw.Text == "get") { getBody = body; getSpan = kw.Span; }
            else                  { setBody = body; setSpan = kw.Span; }
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        if (getBody is null && setBody is null)
            throw new ParseException(
                "indexer must declare at least one of `get` or `set`",
                start, DiagnosticCodes.UnexpectedToken);
        var result = new List<FunctionDecl>();
        if (getBody is not null)
            result.Add(new FunctionDecl("get_Item", indexParams, elemType, getBody,
                vis, FunctionModifiers.None, null, getSpan));
        if (setBody is not null)
        {
            var setParams = new List<Param>(indexParams) {
                new Param("value", elemType, null, setSpan)
            };
            result.Add(new FunctionDecl("set_Item", setParams, new VoidType(setSpan), setBody,
                vis, FunctionModifiers.None, null, setSpan));
        }
        return result;
    }

    // ── Auto-property accessors ──────────────────────────────────────────────

    /// Parse auto-property accessor block `{ get; [set;] }` (order-insensitive).
    /// Returns (hasGet, hasSet). At least one accessor must be present.
    /// Accessor visibility (`public get; private set;`) parsed but ignored
    /// — uniform with indexer (L3-G4e).
    private static (bool hasGet, bool hasSet) ParseAutoPropAccessors(
        ref TokenCursor cursor, Visibility memberVis, Span propSpan)
    {
        ExpectKind(ref cursor, TokenKind.LBrace);
        bool hasGet = false, hasSet = false;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            ParseVisibility(ref cursor, memberVis); // accessor vis ignored
            var kw = cursor.Current;
            if (kw.Kind != TokenKind.Identifier || (kw.Text != "get" && kw.Text != "set"))
                throw new ParseException(
                    $"expected `get` or `set` in auto-property body, got `{kw.Text}`",
                    kw.Span, DiagnosticCodes.UnexpectedToken);
            cursor = cursor.Advance();
            ExpectKind(ref cursor, TokenKind.Semicolon);
            if (kw.Text == "get") hasGet = true; else hasSet = true;
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        if (!hasGet)
            throw new ParseException(
                "auto-property must declare `get` (set-only properties are not supported)",
                propSpan, DiagnosticCodes.UnexpectedToken);
        return (hasGet, hasSet);
    }

    /// Synthesise (FieldDecl, getter, [setter]) for a class auto-property
    /// `<vis>? <type> <name> { get; [set;] }`. Returns:
    ///   - backing FieldDecl: `private <type> __prop_<name>;`
    ///   - getter: `<vis> <type> get_<name>() { return this.__prop_<name>; }`
    ///   - setter (if hasSet): `<vis> void set_<name>(<type> value) { this.__prop_<name> = value; }`
    internal static (FieldDecl backing, List<FunctionDecl> accessors)
    SynthesizeClassAutoProp(
        ref TokenCursor cursor, Visibility memberVis, bool isStatic,
        TypeExpr propType, string propName, Span propSpan)
    {
        var (_, hasSet) = ParseAutoPropAccessors(ref cursor, memberVis, propSpan);

        var bfName  = $"__prop_{propName}";
        var backing = new FieldDecl(bfName, propType, Visibility.Private, isStatic, null, propSpan);

        var mods = isStatic ? FunctionModifiers.Static : FunctionModifiers.None;

        // getter body: return this.__prop_<name>;
        var thisExpr = new IdentExpr("this", propSpan);
        var bfRead   = new MemberExpr(thisExpr, bfName, propSpan);
        var getBody  = new BlockStmt(new List<Stmt> { new ReturnStmt(bfRead, propSpan) }, propSpan);
        var getter   = new FunctionDecl($"get_{propName}",
            new List<Param>(), propType, getBody, memberVis, mods, null, propSpan);

        var accessors = new List<FunctionDecl> { getter };

        if (hasSet)
        {
            // setter body: this.__prop_<name> = value;
            var valExpr  = new IdentExpr("value", propSpan);
            var bfWrite  = new MemberExpr(thisExpr, bfName, propSpan);
            var assign   = new AssignExpr(bfWrite, valExpr, propSpan);
            var setBody  = new BlockStmt(new List<Stmt> { new ExprStmt(assign, propSpan) }, propSpan);
            var setter   = new FunctionDecl($"set_{propName}",
                new List<Param> { new Param("value", propType, null, propSpan) },
                new VoidType(propSpan), setBody, memberVis, mods, null, propSpan);
            accessors.Add(setter);
        }
        return (backing, accessors);
    }

    /// 2026-05-03 add-event-keyword-multicast (D2c-多播) +
    /// 2026-05-03 add-multicast-func-predicate (D2d-1)：合成 event field 的
    /// auto-init + add_X / remove_X 方法。同 `SynthesizeClassAutoProp` 模式。
    ///
    /// 接受三种多播类型（D2d-1 放宽）：
    ///   event MulticastAction<T> X;     handler 类型 Action<T>
    ///   event MulticastFunc<T,R> X;     handler 类型 Func<T,R>
    ///   event MulticastPredicate<T> X;  handler 类型 Predicate<T>
    ///
    /// 单播 event（`event Action<T> X;`）暂不支持 —— 报 "not yet supported"。
    /// Spec 2b `add-event-keyword-singlecast` 落地。
    private static readonly HashSet<string> MulticastTypeNames =
        new(StringComparer.Ordinal) { "MulticastAction", "MulticastFunc", "MulticastPredicate" };

    // 2026-05-04 add-event-keyword-singlecast (D-7)：单播 event 类型集合。
    private static readonly HashSet<string> SinglecastTypeNames =
        new(StringComparer.Ordinal) { "Action", "Func", "Predicate" };

    private static string HandlerTypeName(string multicastTypeName) => multicastTypeName switch
    {
        "MulticastAction"    => "Action",
        "MulticastFunc"      => "Func",
        "MulticastPredicate" => "Predicate",
        _ => throw new InvalidOperationException($"not a multicast type: {multicastTypeName}"),
    };

    internal static (FieldDecl backing, List<FunctionDecl> accessors)
    SynthesizeClassEvent(FieldDecl evtField)
    {
        var fieldName = evtField.Name;
        var fieldType = evtField.Type;
        var span      = evtField.Span;

        // 验证类型：多播 / 单播两类
        if (fieldType is not GenericType gt || gt.TypeArgs.Count == 0)
        {
            throw new ParseException(
                "event field type must be `Action<T>` / `Func<T,R>` / `Predicate<T>` (single-cast) or `MulticastAction<T>` / `MulticastFunc<T,R>` / `MulticastPredicate<T>` (multi-cast)",
                span, DiagnosticCodes.UnexpectedToken);
        }
        bool isMulticast  = MulticastTypeNames.Contains(gt.Name);
        bool isSinglecast = SinglecastTypeNames.Contains(gt.Name);
        if (!isMulticast && !isSinglecast)
        {
            throw new ParseException(
                $"event field type `{gt.Name}` not supported; use Action / Func / Predicate (single-cast) or Multicast{{Action|Func|Predicate}} (multi-cast)",
                span, DiagnosticCodes.UnexpectedToken);
        }

        if (isMulticast)
        {
            // ── 多播路径（D2c-多播 + D2d-1） ────────────────────────────
            // Backing field: 同名 + auto-init `new MulticastXxx<...>()`
            var mInitExpr = new NewExpr(fieldType, new List<Expr>(), span);
            var mBacking  = new FieldDecl(
                fieldName, fieldType, evtField.Visibility, evtField.IsStatic,
                mInitExpr, span, IsEvent: true);

            // handler 类型：Action<T> / Func<T,R> / Predicate<T>
            var mHandlerT = new GenericType(HandlerTypeName(gt.Name), gt.TypeArgs, span);
            var mIdisp    = new NamedType("IDisposable", span);

            // add_X body: return this.X.Subscribe(h);
            var addThis        = new IdentExpr("this", span);
            var addFieldRead   = new MemberExpr(addThis, fieldName, span);
            var addSubMember   = new MemberExpr(addFieldRead, "Subscribe", span);
            var addHRef        = new IdentExpr("h", span);
            var addSubCall     = new CallExpr(addSubMember, new List<Expr> { addHRef }, span);
            var addBody        = new BlockStmt(
                new List<Stmt> { new ReturnStmt(addSubCall, span) }, span);
            var addFn = new FunctionDecl(
                $"add_{fieldName}",
                new List<Param> { new Param("h", mHandlerT, null, span) },
                mIdisp, addBody,
                Visibility.Public,
                FunctionModifiers.None,
                null, span);

            // remove_X body: this.X.Unsubscribe(h);
            var rmThis        = new IdentExpr("this", span);
            var rmFieldRead   = new MemberExpr(rmThis, fieldName, span);
            var rmUnsubMember = new MemberExpr(rmFieldRead, "Unsubscribe", span);
            var rmHRef        = new IdentExpr("h", span);
            var rmUnsubCall   = new CallExpr(rmUnsubMember, new List<Expr> { rmHRef }, span);
            var rmBody        = new BlockStmt(
                new List<Stmt> { new ExprStmt(rmUnsubCall, span) }, span);
            var rmFn = new FunctionDecl(
                $"remove_{fieldName}",
                new List<Param> { new Param("h", mHandlerT, null, span) },
                new VoidType(span), rmBody,
                Visibility.Public,
                FunctionModifiers.None,
                null, span);

            return (mBacking, new List<FunctionDecl> { addFn, rmFn });
        }

        // ── 单播路径（D-7 add-event-keyword-singlecast + D-7-residual） ─
        // Backing field: 同名 + OptionType wrap，初值 null（OptionType 默认）
        var optionType = new OptionType(fieldType, span);
        var sBacking   = new FieldDecl(
            fieldName, optionType, evtField.Visibility, evtField.IsStatic,
            null, span, IsEvent: true);

        // 单播 handler 类型 = 字段裸类型（Action<T> 自身）
        var sHandlerT = fieldType;
        var sIdisp    = new NamedType("IDisposable", span);

        // add_X body (D-7-residual)：
        //   if (this.X != null)
        //       throw new InvalidOperationException("single-cast event already bound");
        //   this.X = h;
        //   return Disposable.From(() => this.remove_X(h));
        var sAddThis      = new IdentExpr("this", span);
        var sAddFieldRead = new MemberExpr(sAddThis, fieldName, span);
        var sAddNullCmp   = new BinaryExpr("!=", sAddFieldRead, new LitNullExpr(span), span);
        var ioeName       = new NamedType("InvalidOperationException", span);
        var ioeArg        = new LitStrExpr("single-cast event already bound", span);
        var ioeNew        = new NewExpr(ioeName, new List<Expr> { ioeArg }, span);
        var sAddThrow     = new ThrowStmt(ioeNew, span);
        var sAddIfBlock   = new BlockStmt(new List<Stmt> { sAddThrow }, span);
        var sAddIf        = new IfStmt(sAddNullCmp, sAddIfBlock, null, span);
        var sAddFieldRead2 = new MemberExpr(new IdentExpr("this", span), fieldName, span);
        var sAddAssign    = new AssignExpr(sAddFieldRead2, new IdentExpr("h", span), span);
        // Disposable.From(() => this.remove_X(h)) —— 通过 lambda 捕获 this + h。
        // Lambda 调 remove_X 而非直接 `this.X = null`，复用 D-7 主体 remove_X 中
        // ref-equality 比较，避免重复实现。
        var lambdaThis     = new IdentExpr("this", span);
        var lambdaRmMember = new MemberExpr(lambdaThis, $"remove_{fieldName}", span);
        var lambdaH        = new IdentExpr("h", span);
        var lambdaRmCall   = new CallExpr(lambdaRmMember, new List<Expr> { lambdaH }, span);
        var lambda         = new LambdaExpr(
            new List<LambdaParam>(), new LambdaExprBody(lambdaRmCall, span), span);
        var disposableId   = new IdentExpr("Disposable", span);
        var disposableFrom = new MemberExpr(disposableId, "From", span);
        var disposableCall = new CallExpr(disposableFrom, new List<Expr> { lambda }, span);
        var sAddReturn     = new ReturnStmt(disposableCall, span);
        var sAddBody      = new BlockStmt(
            new List<Stmt> { sAddIf, new ExprStmt(sAddAssign, span), sAddReturn }, span);
        var sAddFn = new FunctionDecl(
            $"add_{fieldName}",
            new List<Param> { new Param("h", sHandlerT, null, span) },
            sIdisp, sAddBody,
            Visibility.Public,
            FunctionModifiers.None,
            null, span);

        // remove_X body:
        //   if (DelegateOps.ReferenceEquals(this.X, h)) this.X = null;
        var sRmThis        = new IdentExpr("this", span);
        var sRmFieldRead   = new MemberExpr(sRmThis, fieldName, span);
        var sRmDelegateOps = new IdentExpr("DelegateOps", span);
        var sRmRefEqMember = new MemberExpr(sRmDelegateOps, "ReferenceEquals", span);
        var sRmRefEqCall   = new CallExpr(sRmRefEqMember,
            new List<Expr> { sRmFieldRead, new IdentExpr("h", span) }, span);
        var sRmFieldRead2  = new MemberExpr(new IdentExpr("this", span), fieldName, span);
        var sRmAssignNull  = new AssignExpr(sRmFieldRead2, new LitNullExpr(span), span);
        var sRmIfBlock     = new BlockStmt(
            new List<Stmt> { new ExprStmt(sRmAssignNull, span) }, span);
        var sRmIf          = new IfStmt(sRmRefEqCall, sRmIfBlock, null, span);
        var sRmBody        = new BlockStmt(new List<Stmt> { sRmIf }, span);
        var sRmFn = new FunctionDecl(
            $"remove_{fieldName}",
            new List<Param> { new Param("h", sHandlerT, null, span) },
            new VoidType(span), sRmBody,
            Visibility.Public,
            FunctionModifiers.None,
            null, span);

        return (sBacking, new List<FunctionDecl> { sAddFn, sRmFn });
    }

    /// 2026-05-03 add-interface-event-default：interface 端 event 合成。
    /// 与 class 端 `SynthesizeClassEvent` 对偶，但产 `MethodSignature`（无 body）
    /// 而非 `FunctionDecl`。每个 multicast event 产 add_X / remove_X 两个
    /// instance abstract signature。
    /// 单播 event 同样报 "single-cast event not yet supported"。
    internal static List<MethodSignature> SynthesizeInterfaceEvent(
        TypeExpr fieldType, string fieldName, Span span)
    {
        // 2026-05-03 add-multicast-func-predicate (D2d-1) +
        // 2026-05-04 add-event-keyword-singlecast (D-7)：接受多播 + 单播两类。
        if (fieldType is not GenericType gt || gt.TypeArgs.Count == 0)
        {
            throw new ParseException(
                "event field type must be `Action<T>` / `Func<T,R>` / `Predicate<T>` (single-cast) or `MulticastAction<T>` / `MulticastFunc<T,R>` / `MulticastPredicate<T>` (multi-cast)",
                span, DiagnosticCodes.UnexpectedToken);
        }
        bool isMulticast  = MulticastTypeNames.Contains(gt.Name);
        bool isSinglecast = SinglecastTypeNames.Contains(gt.Name);
        if (!isMulticast && !isSinglecast)
        {
            throw new ParseException(
                $"event field type `{gt.Name}` not supported in interface; use Action / Func / Predicate (single-cast) or Multicast{{Action|Func|Predicate}} (multi-cast)",
                span, DiagnosticCodes.UnexpectedToken);
        }

        // 多播：handler 类型 = `<HandlerName><...args>`；返回 IDisposable
        // 单播：handler 类型 = 字段裸类型；返回 IDisposable（D-7-residual：单播也对齐）
        var handlerT = isMulticast
            ? new GenericType(HandlerTypeName(gt.Name), gt.TypeArgs, span)
            : (TypeExpr)fieldType;
        var addRet   = (TypeExpr)new NamedType("IDisposable", span);
        var hParam   = new Param("h", handlerT, null, span);
        return new List<MethodSignature>
        {
            new MethodSignature($"add_{fieldName}",    new List<Param> { hParam }, addRet,             span, IsStatic: false, IsVirtual: false, Body: null),
            new MethodSignature($"remove_{fieldName}", new List<Param> { hParam }, new VoidType(span), span, IsStatic: false, IsVirtual: false, Body: null),
        };
    }

    /// Parse auto-property in an interface body — produces method signatures only
    /// (no backing field, no body).
    /// Returns (getter [+ setter] MethodSignatures).
    internal static List<MethodSignature> SynthesizeInterfaceAutoProp(
        ref TokenCursor cursor, Visibility memberVis, bool isStatic,
        TypeExpr propType, string propName, Span propSpan)
    {
        var (_, hasSet) = ParseAutoPropAccessors(ref cursor, memberVis, propSpan);
        var result = new List<MethodSignature>
        {
            new MethodSignature($"get_{propName}", new List<Param>(), propType, propSpan, isStatic, false, null),
        };
        if (hasSet)
        {
            var setParams = new List<Param> { new Param("value", propType, null, propSpan) };
            result.Add(new MethodSignature($"set_{propName}", setParams, new VoidType(propSpan),
                propSpan, isStatic, false, null));
        }
        return result;
    }
}
