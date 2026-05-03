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

    /// 2026-05-03 add-event-keyword-multicast (D2c-多播)：合成 event field 的
    /// auto-init + add_X / remove_X 方法。同 `SynthesizeClassAutoProp` 模式。
    ///
    /// 多播 `event MulticastAction<T> X;` →
    ///   field X: MulticastAction<T> = new MulticastAction<T>();
    ///   public IDisposable add_X(Action<T> h) { return this.X.Subscribe(h); }
    ///   public void remove_X(Action<T> h) { this.X.Unsubscribe(h); }
    ///
    /// 单播 event（`event Action<T> X;`）暂不支持 —— 报 "not yet supported"。
    /// Spec 2b `add-event-keyword-singlecast` 落地。
    internal static (FieldDecl backing, List<FunctionDecl> accessors)
    SynthesizeClassEvent(FieldDecl evtField)
    {
        var fieldName = evtField.Name;
        var fieldType = evtField.Type;
        var span      = evtField.Span;

        // 验证类型：必须是 GenericType("MulticastAction", [T])
        if (fieldType is not GenericType gt
            || gt.Name != "MulticastAction"
            || gt.TypeArgs.Count != 1)
        {
            throw new ParseException(
                "single-cast event not yet supported (D2c-singlecast pending); use `event MulticastAction<T>` for now",
                span, DiagnosticCodes.UnexpectedToken);
        }

        var typeArg = gt.TypeArgs[0];  // T

        // Backing field: 同名 + auto-init `new MulticastAction<T>()`
        var initExpr = new NewExpr(fieldType, new List<Expr>(), span);
        var backing  = new FieldDecl(
            fieldName, fieldType, evtField.Visibility, evtField.IsStatic,
            initExpr, span, IsEvent: true);

        // Action<T> param type + IDisposable return type
        var actionT = new GenericType("Action", new List<TypeExpr> { typeArg }, span);
        var idisp   = new NamedType("IDisposable", span);

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
            new List<Param> { new Param("h", actionT, null, span) },
            idisp, addBody,
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
            new List<Param> { new Param("h", actionT, null, span) },
            new VoidType(span), rmBody,
            Visibility.Public,
            FunctionModifiers.None,
            null, span);

        return (backing, new List<FunctionDecl> { addFn, rmFn });
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
