using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// 顶层类型声明解析：enum / interface / class / struct / record / impl。
/// 与 TopLevelParser.cs（CompilationUnit + 函数声明）配套。
internal static partial class TopLevelParser
{
    // ── Enum ──────────────────────────────────────────────────────────────────

    private static EnumDecl ParseEnumDecl(ref TokenCursor cursor, DiagnosticBag? diags = null)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        SkipNonVisibilityModifiers(ref cursor);
        ExpectKind(ref cursor, TokenKind.Enum);
        var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;

        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();  // skip base type token
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.LBrace);

        var members = new List<EnumMember>();
        long next   = 0;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            var mSpan = cursor.Current.Span;
            if (cursor.Current.Kind is TokenKind.Public or TokenKind.Private
                                    or TokenKind.Protected or TokenKind.Internal)
            {
                if (diags != null)
                {
                    diags.Error(DiagnosticCodes.InvalidModifier,
                        "enum members cannot have access modifiers", cursor.Current.Span);
                    while (!cursor.IsEnd && cursor.Current.Kind != TokenKind.Comma
                                        && cursor.Current.Kind != TokenKind.RBrace)
                        cursor = cursor.Advance();
                    if (cursor.Current.Kind == TokenKind.Comma) cursor = cursor.Advance();
                    continue;
                }
                throw new ParseException("enum members cannot have access modifiers",
                    cursor.Current.Span,
                    DiagnosticCodes.InvalidModifier);
            }

            var mName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            long? val;
            if (cursor.Current.Kind == TokenKind.Eq)
            {
                cursor = cursor.Advance();
                bool neg = cursor.Current.Kind == TokenKind.Minus;
                if (neg) cursor = cursor.Advance();
                long raw = long.Parse(ExpectKind(ref cursor, TokenKind.IntLiteral).Text);
                val  = neg ? -raw : raw;
                next = val.Value + 1;
            }
            else
            {
                val = next++;
            }
            members.Add(new EnumMember(mName, val, mSpan));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new EnumDecl(name, vis, members, start);
    }

    // ── Delegate (2026-05-02 add-delegate-type) ──────────────────────────────
    //
    // 语法：`[visibility] delegate ReturnType Name<TypeParams>?(Params) where? ;`
    // 顶层 + 嵌套（class body 内）共用此 parser。
    private static DelegateDecl ParseDelegateDecl(
        ref TokenCursor cursor, LanguageFeatures feat,
        Visibility defaultVis = Visibility.Internal)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, defaultVis);
        SkipNonVisibilityModifiers(ref cursor);
        ExpectKind(ref cursor, TokenKind.Delegate);

        // D1a Decision 8: `delegate*<T,R>` (unmanaged func pointer) 预留
        if (cursor.Current.Kind == TokenKind.Star)
            throw new ParseException(
                "`delegate*` (unmanaged function pointer) is not yet supported",
                cursor.Current.Span);

        var retType = TypeParser.Parse(cursor).Unwrap(ref cursor);
        var name    = ExpectKind(ref cursor, TokenKind.Identifier).Text;
        var typeParams = ParseTypeParams(ref cursor);   // null if no `<...>`
        var parms      = ParseParamList(ref cursor, feat);
        var where      = ParseWhereClause(ref cursor);  // null if no `where`
        ExpectKind(ref cursor, TokenKind.Semicolon);
        return new DelegateDecl(name, vis, parms, retType,
            start, typeParams, where);
    }

    // ── Interface ─────────────────────────────────────────────────────────────

    private static InterfaceDecl ParseInterfaceDecl(
        ref TokenCursor cursor, LanguageFeatures feat)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        SkipNonVisibilityModifiers(ref cursor);
        ExpectKind(ref cursor, TokenKind.Interface);
        var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;

        // Generic params: interface IFoo<T>
        var typeParams = ParseTypeParams(ref cursor);

        // Skip base interfaces: interface IFoo : IBar, IBaz
        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();
            ParseQualifiedName(ref cursor);
            SkipGenericParams(ref cursor);
            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                ParseQualifiedName(ref cursor);
                SkipGenericParams(ref cursor);
            }
        }

        var whereClause = ParseWhereClause(ref cursor);

        ExpectKind(ref cursor, TokenKind.LBrace);
        var methods = new List<MethodSignature>();
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) { SkipAttribute(ref cursor); continue; }
            var mSpan = cursor.Current.Span;
            ParseVisibility(ref cursor, Visibility.Public);
            // L3 三档静态成员：static abstract / static virtual / static（无修饰）
            var (isStatic, isVirtual, isOverride, isAbstract, _, _) = ParseNonVisibilityModifiers(ref cursor);
            if (isOverride)
                throw new ParseException(
                    "`override` is not valid in interface body — implementers use `static override` at the class",
                    mSpan, DiagnosticCodes.InvalidModifier);
            if (isAbstract && isVirtual)
                throw new ParseException(
                    "`abstract` and `virtual` cannot be combined", mSpan,
                    DiagnosticCodes.InvalidModifier);
            // 2026-05-03 add-interface-event-default：interface 内 `event`
            // 声明合成 add_X / remove_X MethodSignatures（与 class 端
            // SynthesizeClassEvent 对偶）。
            if (cursor.Current.Kind == TokenKind.Event)
            {
                cursor = cursor.Advance();
                var evtType = TypeParser.Parse(cursor).Unwrap(ref cursor);
                var evtName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
                ExpectKind(ref cursor, TokenKind.Semicolon);
                methods.AddRange(SynthesizeInterfaceEvent(evtType, evtName, mSpan));
                continue;
            }
            var mType = TypeParser.Parse(cursor).Unwrap(ref cursor);
            // L3 operator overload in interface: `static abstract T operator +(T, T)`
            string mName;
            if (cursor.Current.Kind == TokenKind.Operator)
            {
                cursor = cursor.Advance();
                mName = ParseOperatorSymbolAsMethodName(ref cursor);
            }
            else
            {
                mName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            }

            // Property in interface body: `T Name { get; [set;] }` desugars to
            // method signatures (no body). 取 vis=Public 作为 interface member 默认。
            if (cursor.Current.Kind == TokenKind.LBrace
                && cursor.Peek(1).Kind != TokenKind.RBrace
                && cursor.Peek(1).Text == "get")
            {
                var propSigs = SynthesizeInterfaceAutoProp(
                    ref cursor, Visibility.Public, isStatic, mType, mName, mSpan);
                methods.AddRange(propSigs);
                continue;
            }

            var parms = ParseParamList(ref cursor, feat);
            // L3 三档静态：有 body 的 static 方法（virtual 或 concrete）parse body；否则 `;`
            BlockStmt? body = null;
            if (cursor.Current.Kind == TokenKind.LBrace)
            {
                body = StmtParser.ParseBlock(cursor, feat).Unwrap(ref cursor);
            }
            else
            {
                ExpectKind(ref cursor, TokenKind.Semicolon);
            }
            // Tier validation: abstract 禁有 body；virtual 必须有 body；
            // instance（非 static）iter 1 不支持 default body。
            if (isAbstract && body != null)
                throw new ParseException(
                    "`abstract` interface member cannot have a body", mSpan,
                    DiagnosticCodes.InvalidModifier);
            if (isVirtual && body == null)
                throw new ParseException(
                    "`virtual` interface member must provide a default body", mSpan,
                    DiagnosticCodes.InvalidModifier);
            if (!isStatic && body != null)
                throw new ParseException(
                    "instance default interface methods are not supported in this iteration — use `static virtual` instead",
                    mSpan, DiagnosticCodes.InvalidModifier);
            methods.Add(new MethodSignature(mName, parms, mType, mSpan, isStatic, isVirtual, body));
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new InterfaceDecl(name, vis, methods, start, typeParams, whereClause);
    }

    // ── Class / struct / record ───────────────────────────────────────────────

    private static ClassDecl ParseClassDecl(ref TokenCursor cursor, LanguageFeatures feat,
        DiagnosticBag? diags = null, NativeAttribute? classNative = null)
    {
        // Spec C9 — class-level [Native(lib=, type=)] becomes the per-class
        // default that methods inside this class can stitch against.
        Tier1NativeBinding? classDefaults = classNative?.Tier1;

        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        var (_, _, _, isAbstract, isSealed, _) = ParseNonVisibilityModifiers(ref cursor);

        bool isRecord = false;
        if (cursor.Current.Kind == TokenKind.Record) { isRecord = true; cursor = cursor.Advance(); }

        bool isStruct = cursor.Current.Kind == TokenKind.Struct;
        if (cursor.Current.Kind is TokenKind.Class or TokenKind.Struct) cursor = cursor.Advance();

        // L3-G4b primitive-as-struct: allow `struct int { ... }`, `struct double { ... }` etc.
        var name = ExpectTypeDeclName(ref cursor).Text;

        // Generic params: class Foo<T>, struct Pair<K,V>
        var typeParams = ParseTypeParams(ref cursor);

        var fields  = new List<FieldDecl>();
        var methods = new List<FunctionDecl>();

        // Positional record: record Person(string Name, int Age)
        if (isRecord && cursor.Current.Kind == TokenKind.LParen)
        {
            cursor = cursor.Advance();
            var ctorParams = new List<Param>();
            var ctorStmts  = new List<Stmt>();
            while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
            {
                var pSpan = cursor.Current.Span;
                ParseVisibility(ref cursor, Visibility.Public);
                var pType = TypeParser.Parse(cursor).Unwrap(ref cursor);
                var pName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
                ctorParams.Add(new Param(pName, pType, null, pSpan));
                fields.Add(new FieldDecl(pName, pType, Visibility.Public, false, null, pSpan));
                ctorStmts.Add(new ExprStmt(
                    new AssignExpr(
                        new MemberExpr(new IdentExpr("this", pSpan), pName, pSpan),
                        new IdentExpr(pName, pSpan), pSpan),
                    pSpan));
                if (cursor.Current.Kind != TokenKind.Comma) break;
                cursor = cursor.Advance();
            }
            ExpectKind(ref cursor, TokenKind.RParen);
            var ctorBody = new BlockStmt(ctorStmts, start);
            methods.Add(new FunctionDecl(name, ctorParams, new VoidType(start),
                ctorBody, Visibility.Public, FunctionModifiers.None, null, start));

            if (cursor.Current.Kind == TokenKind.Semicolon)
            {
                cursor = cursor.Advance();
                return new ClassDecl(name, isStruct, isRecord, isAbstract, isSealed, vis,
                    null, [], fields, methods, start, typeParams, Where: null,
                    ClassNativeDefaults: classDefaults);
            }
        }

        // Base class / interfaces: class Foo : Base, IFace<T>, IBar
        // First entry may be a base class (non-`I`-prefix) or an interface;
        // subsequent entries are all interfaces. L3-G2.5 chain: interface
        // type args are preserved in the AST (parsed as full TypeExpr).
        string? baseClass   = null;
        var     ifaces      = new List<TypeExpr>();
        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();
            var firstSpan = cursor.Current.Span;
            var firstTy   = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var firstName = ExtractTypeName(firstTy);
            if (firstName.Length > 1 && firstName[0] == 'I' && char.IsUpper(firstName[1]))
                ifaces.Add(firstTy);
            else
                baseClass = firstName;

            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                ifaces.Add(TypeParser.Parse(cursor).Unwrap(ref cursor));
            }
        }

        var classWhereClause = ParseWhereClause(ref cursor);

        ExpectKind(ref cursor, TokenKind.LBrace);
        NativeAttribute?     pendingNative    = null;
        List<TestAttribute>? pendingTestAttrs = null;  // R1: per-method z42.test.* attrs
        var nestedDelegates = new List<DelegateDecl>(); // 2026-05-02 add-delegate-type
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            try
            {
                if (cursor.Current.Kind == TokenKind.LBracket)
                {
                    var (parsedNative, parsedTest) = TryParseAttribute(ref cursor);
                    if (parsedNative != null) pendingNative = parsedNative;
                    if (parsedTest   != null) (pendingTestAttrs ??= new()).Add(parsedTest);
                    continue;
                }
                // 2026-05-02 add-delegate-type: 嵌套 delegate (class body 内)
                if (IsDelegateDecl(cursor))
                {
                    pendingNative = null;
                    pendingTestAttrs = null;
                    nestedDelegates.Add(ParseDelegateDecl(ref cursor, feat, Visibility.Public));
                    continue;
                }
                // L3-G4e: indexer `<vis>? <type> this [params] { get {..} set {..} }`
                // desugars to `get_Item(params) → type` + `set_Item(params, type value) → void`
                if (IsIndexerDecl(cursor))
                {
                    pendingNative = null;
                    pendingTestAttrs = null;
                    foreach (var m in ParseIndexerDecl(ref cursor, feat, diags))
                        methods.Add(m);
                    continue;
                }
                if (IsFieldDecl(cursor))
                {
                    pendingNative = null;
                    pendingTestAttrs = null;
                    var fSpan = cursor.Current.Span;
                    var fVis  = ParseVisibility(ref cursor, Visibility.Internal);
                    var (fStatic, _, _, _, _, _) = ParseNonVisibilityModifiers(ref cursor);
                    // 2026-05-03 add-event-keyword (D2c)：`event` modifier 在 vis +
                    // 非 vis modifiers 之后、type 之前；标识字段为 event field。
                    bool fIsEvent = false;
                    if (cursor.Current.Kind == TokenKind.Event)
                    {
                        cursor = cursor.Advance();
                        fIsEvent = true;
                    }
                    var fType = TypeParser.Parse(cursor).Unwrap(ref cursor);
                    var fName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
                    Expr? fInit = null;
                    if (cursor.Current.Kind == TokenKind.LBrace)
                    {
                        // Auto-property: synthesize backing field + accessors.
                        if (fIsEvent)
                            throw new ParseException(
                                "`event` modifier cannot be combined with auto-property syntax",
                                fSpan, DiagnosticCodes.InvalidModifier);
                        var (backing, accessors) = SynthesizeClassAutoProp(
                            ref cursor, fVis, fStatic, fType, fName, fSpan);
                        fields.Add(backing);
                        foreach (var acc in accessors) methods.Add(acc);
                        continue;
                    }
                    if (cursor.Current.Kind == TokenKind.Eq)
                    {
                        cursor = cursor.Advance();
                        fInit = ExprParser.Parse(cursor, LanguageFeatures.Phase1).Unwrap(ref cursor);
                    }
                    ExpectKind(ref cursor, TokenKind.Semicolon);
                    var rawField = new FieldDecl(fName, fType, fVis, fStatic, fInit, fSpan, fIsEvent);
                    if (fIsEvent)
                    {
                        // 2026-05-03 D2c-多播：合成 add_X / remove_X 方法 + auto-init
                        var (backing, accessors) = SynthesizeClassEvent(rawField);
                        fields.Add(backing);
                        foreach (var acc in accessors) methods.Add(acc);
                    }
                    else
                    {
                        fields.Add(rawField);
                    }
                }
                else
                {
                    methods.Add(ParseFunctionDecl(ref cursor, feat, Visibility.Internal, pendingNative, diags, pendingTestAttrs));
                    pendingNative    = null;
                    pendingTestAttrs = null;
                }
            }
            catch (ParseException ex) when (diags != null)
            {
                diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
                pendingNative = null;
                if (!cursor.IsEnd) cursor = cursor.Advance();
                while (!cursor.IsEnd && cursor.Current.Kind != TokenKind.RBrace
                                     && cursor.Current.Kind != TokenKind.Semicolon)
                    cursor = cursor.Advance();
                if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
            }
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new ClassDecl(name, isStruct, isRecord, isAbstract, isSealed, vis,
            baseClass, ifaces, fields, methods, start, typeParams, classWhereClause,
            ClassNativeDefaults: classDefaults,
            NestedDelegates: nestedDelegates.Count == 0 ? null : nestedDelegates);
    }

    // ── Extern impl block ─────────────────────────────────────────────────────

    /// Parse `impl <TraitType> for <TargetType> { <methods> }`.
    /// Methods must have a body — `extern` is intentionally NOT allowed inside
    /// impl blocks. `extern` (`[Native]`) binds VM intrinsics / host FFI and
    /// belongs at the type-definition site (e.g. `Int.z42`'s struct body), not
    /// in cross-cutting impl extensions. `impl` exists for organizational
    /// separation + cross-package extension via script body that wraps existing
    /// type APIs. (Decision 2026-04-26 — see docs/design/generics.md.)
    private static ImplDecl ParseImplDecl(ref TokenCursor cursor, LanguageFeatures feat,
        DiagnosticBag? diags = null)
    {
        var start = cursor.Current.Span;
        ExpectKind(ref cursor, TokenKind.Impl);
        var traitType = TypeParser.Parse(cursor).Unwrap(ref cursor);
        ExpectKind(ref cursor, TokenKind.For);
        var targetType = TypeParser.Parse(cursor).Unwrap(ref cursor);

        var methods = new List<FunctionDecl>();
        ExpectKind(ref cursor, TokenKind.LBrace);
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            try
            {
                var m = ParseFunctionDecl(ref cursor, feat, Visibility.Public, null, diags);
                if (m.IsExtern)
                    throw new ParseException(
                        "extern methods are not allowed in impl blocks; native bindings belong in the type definition",
                        m.Span, DiagnosticCodes.FeatureDisabled);
                methods.Add(m);
            }
            catch (ParseException ex) when (diags != null)
            {
                diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
                if (!cursor.IsEnd) cursor = cursor.Advance();
                while (!cursor.IsEnd && cursor.Current.Kind != TokenKind.RBrace
                                     && cursor.Current.Kind != TokenKind.Semicolon)
                    cursor = cursor.Advance();
                if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
            }
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new ImplDecl(traitType, targetType, methods, start);
    }
}
