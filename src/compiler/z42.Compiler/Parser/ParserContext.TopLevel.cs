using System.Text;
using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// Top-level compilation unit parsing — part of the ParserContext partial class.
/// Handles: namespace, using, class, struct, enum, function declarations, and types.
internal sealed partial class ParserContext
{
    // ── Compilation unit ──────────────────────────────────────────────────────

    internal CompilationUnit ParseCompilationUnit()
    {
        var start  = Current.Span;
        string? ns = null;
        var usings = new List<string>();

        if (Match(TokenKind.Namespace))
        {
            ns = ParseQualifiedName();
            Match(TokenKind.Semicolon);
        }

        while (Check(TokenKind.Using))
        {
            Advance();
            usings.Add(ParseQualifiedName());
            Match(TokenKind.Semicolon);
        }

        var classes    = new List<ClassDecl>();
        var functions  = new List<FunctionDecl>();
        var enums      = new List<EnumDecl>();
        var interfaces = new List<InterfaceDecl>();
        while (!Check(TokenKind.Eof))
        {
            if (Check(TokenKind.LBracket)) { SkipAttribute(); continue; }
            if (Check(TokenKind.Namespace)) { Advance(); ParseQualifiedName(); Match(TokenKind.Semicolon); continue; }
            if (Check(TokenKind.Using))     { Advance(); ParseQualifiedName(); Match(TokenKind.Semicolon); continue; }
            if (IsEnumDecl())               { enums.Add(ParseEnumDecl()); continue; }
            if (IsInterfaceDecl())          { interfaces.Add(ParseInterfaceDecl()); continue; }
            if (IsClassOrStructDecl())      { classes.Add(ParseClassDecl()); continue; }
            functions.Add(ParseFunctionDecl(Visibility.Internal));
        }

        return new CompilationUnit(ns, usings, classes, functions, enums, interfaces, start);
    }

    // ── Visibility helpers ────────────────────────────────────────────────────

    /// Parses an optional access modifier; returns `defaultVis` if none present.
    /// Throws if two visibility modifiers appear in a row (illegal combination).
    private Visibility ParseVisibility(Visibility defaultVis)
    {
        if (Current.Kind is not (TokenKind.Public or TokenKind.Private
                              or TokenKind.Protected or TokenKind.Internal))
            return defaultVis;

        var vis = Current.Kind switch
        {
            TokenKind.Public    => Visibility.Public,
            TokenKind.Private   => Visibility.Private,
            TokenKind.Protected => Visibility.Protected,
            _                   => Visibility.Internal,   // TokenKind.Internal
        };
        Advance();

        // Detect illegal combined visibility (e.g. "protected internal")
        if (Current.Kind is TokenKind.Public or TokenKind.Private
                         or TokenKind.Protected or TokenKind.Internal)
            throw new ParseException("cannot combine access modifiers", Current.Span);

        return vis;
    }

    /// Parses non-visibility modifiers (static, abstract, sealed, virtual, override, async, new)
    /// and returns which were present.
    private (bool IsStatic, bool IsVirtual, bool IsOverride) ParseNonVisibilityModifiers()
    {
        bool isStatic = false, isVirtual = false, isOverride = false;
        while (Current.Kind is TokenKind.Static or TokenKind.Abstract or TokenKind.Sealed
                            or TokenKind.Virtual or TokenKind.Override or TokenKind.Async
                            or TokenKind.New)
        {
            if (Current.Kind == TokenKind.Static)   isStatic   = true;
            if (Current.Kind == TokenKind.Virtual)  isVirtual  = true;
            if (Current.Kind == TokenKind.Override) isOverride = true;
            Advance();
        }
        return (isStatic, isVirtual, isOverride);
    }

    // ── Enum declaration ──────────────────────────────────────────────────────

    private bool IsEnumDecl()
    {
        int i = 0;
        while (_pos + i < _tokens.Count &&
               _tokens[_pos + i].Kind is TokenKind.Public or TokenKind.Private
                   or TokenKind.Protected or TokenKind.Internal)
            i++;
        return _pos + i < _tokens.Count && _tokens[_pos + i].Kind == TokenKind.Enum;
    }

    private EnumDecl ParseEnumDecl()
    {
        var start = Current.Span;
        var vis = ParseVisibility(Visibility.Internal);
        ParseNonVisibilityModifiers();
        Expect(TokenKind.Enum);
        var name = Expect(TokenKind.Identifier).Text;
        if (Match(TokenKind.Colon)) { Advance(); } // optional `: int` base type
        Expect(TokenKind.LBrace);

        var members = new List<EnumMember>();
        long next   = 0;
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var mSpan = Current.Span;
            // Enum members cannot have access modifiers
            if (Current.Kind is TokenKind.Public or TokenKind.Private
                              or TokenKind.Protected or TokenKind.Internal)
                throw new ParseException(
                    "enum members cannot have access modifiers", Current.Span);
            var mName = Expect(TokenKind.Identifier).Text;
            long? val;
            if (Match(TokenKind.Eq))
            {
                bool neg = Match(TokenKind.Minus);
                long raw = long.Parse(Expect(TokenKind.IntLiteral).Text);
                val  = neg ? -raw : raw;
                next = val.Value + 1;
            }
            else
            {
                val = next++;
            }
            members.Add(new EnumMember(mName, val, mSpan));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RBrace);
        return new EnumDecl(name, vis, members, start);
    }

    // ── Interface declaration ─────────────────────────────────────────────────

    private bool IsInterfaceDecl()
    {
        int i = 0;
        while (_pos + i < _tokens.Count &&
               _tokens[_pos + i].Kind is TokenKind.Public or TokenKind.Private
                   or TokenKind.Protected or TokenKind.Internal)
            i++;
        return _pos + i < _tokens.Count && _tokens[_pos + i].Kind == TokenKind.Interface;
    }

    private InterfaceDecl ParseInterfaceDecl()
    {
        var start = Current.Span;
        var vis = ParseVisibility(Visibility.Internal);
        ParseNonVisibilityModifiers(); // skip abstract, etc.
        Expect(TokenKind.Interface);
        var name = Expect(TokenKind.Identifier).Text;

        // Skip generic parameters: interface IFoo<T>
        if (Check(TokenKind.Lt))
        {
            Advance(); int depth = 1;
            while (!Check(TokenKind.Eof) && depth > 0)
            {
                if (Check(TokenKind.Lt)) depth++;
                if (Check(TokenKind.Gt)) depth--;
                Advance();
            }
        }

        // Skip base interfaces: interface IFoo : IBar, IBaz
        if (Check(TokenKind.Colon))
        {
            Advance();
            do { ParseQualifiedName(); } while (Match(TokenKind.Comma));
        }

        Expect(TokenKind.LBrace);

        var methods = new List<MethodSignature>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            if (Check(TokenKind.LBracket)) { SkipAttribute(); continue; }
            var mSpan = Current.Span;
            ParseVisibility(Visibility.Public);
            ParseNonVisibilityModifiers();
            var mType = ParseTypeExpr();
            var mName = Expect(TokenKind.Identifier).Text;

            if (Check(TokenKind.LBrace))
            {
                SkipAutoPropBody(); // property signature — desugar to backing field concept
                continue;
            }

            Expect(TokenKind.LParen);
            var parms = new List<Param>();
            while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
            {
                var pSpan = Current.Span;
                var pType = ParseTypeExpr();
                var pName = Expect(TokenKind.Identifier).Text;
                parms.Add(new Param(pName, pType, pSpan));
                if (!Match(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RParen);
            Expect(TokenKind.Semicolon);

            methods.Add(new MethodSignature(mName, parms, mType, mSpan));
        }

        Expect(TokenKind.RBrace);
        return new InterfaceDecl(name, vis, methods, start);
    }

    // ── Class / struct declaration ────────────────────────────────────────────

    private bool IsClassOrStructDecl()
    {
        int i = 0;
        while (_pos + i < _tokens.Count &&
               _tokens[_pos + i].Kind is TokenKind.Public or TokenKind.Private
                   or TokenKind.Protected or TokenKind.Internal or TokenKind.Static
                   or TokenKind.Abstract or TokenKind.Sealed)
            i++;
        if (_pos + i >= _tokens.Count) return false;
        return _tokens[_pos + i].Kind is TokenKind.Class or TokenKind.Struct;
    }

    private ClassDecl ParseClassDecl()
    {
        var start = Current.Span;
        var vis = ParseVisibility(Visibility.Internal);
        ParseNonVisibilityModifiers();

        bool isStruct = Current.Kind == TokenKind.Struct;
        Advance(); // consume 'class' or 'struct'
        var name = Expect(TokenKind.Identifier).Text;

        // Skip generic parameters: class Foo<T, U>
        if (Check(TokenKind.Lt))
        {
            Advance(); int depth = 1;
            while (!Check(TokenKind.Eof) && depth > 0)
            {
                if (Check(TokenKind.Lt)) depth++;
                if (Check(TokenKind.Gt)) depth--;
                Advance();
            }
        }

        // Parse base class / interfaces: class Foo : Base, IFace1, IFace2
        string? baseClass  = null;
        var     interfaces = new List<string>();
        if (Match(TokenKind.Colon))
        {
            // First name after ':' — could be base class or interface
            var firstName = ParseQualifiedName();
            // Convention: names starting with 'I' followed by uppercase are interfaces
            if (firstName.Length > 1 && firstName[0] == 'I' && char.IsUpper(firstName[1]))
                interfaces.Add(firstName);
            else
                baseClass = firstName;

            while (Match(TokenKind.Comma))
            {
                var extra = ParseQualifiedName();
                // Skip generic type args on interface names if present
                if (Check(TokenKind.Lt))
                {
                    Advance(); int d = 1;
                    while (!Check(TokenKind.Eof) && d > 0)
                    {
                        if (Check(TokenKind.Lt)) d++;
                        if (Check(TokenKind.Gt)) d--;
                        Advance();
                    }
                }
                interfaces.Add(extra);
            }
        }

        Expect(TokenKind.LBrace);

        var fields  = new List<FieldDecl>();
        var methods = new List<FunctionDecl>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            if (Check(TokenKind.LBracket)) { SkipAttribute(); continue; }
            if (IsFieldDecl())
            {
                var fSpan  = Current.Span;
                var fVis   = ParseVisibility(Visibility.Internal);
                var (fStatic, _, _) = ParseNonVisibilityModifiers();
                var fType  = ParseTypeExpr();
                var fName  = Expect(TokenKind.Identifier).Text;
                if (Check(TokenKind.LBrace))
                    SkipAutoPropBody();  // auto-property: { get; set; } — desugar to backing field
                else
                {
                    if (Match(TokenKind.Eq)) ParseExpr(); // skip optional initializer
                    Expect(TokenKind.Semicolon);
                }
                fields.Add(new FieldDecl(fName, fType, fVis, fStatic, fSpan));
            }
            else
            {
                methods.Add(ParseFunctionDecl(Visibility.Internal));
            }
        }

        Expect(TokenKind.RBrace);
        return new ClassDecl(name, isStruct, vis, baseClass, interfaces, fields, methods, start);
    }

    /// Field: [modifiers] Type Ident (= | ; | { get/set })   vs   method: [modifiers] Type Ident (
    private bool IsFieldDecl()
    {
        int i = 0;
        while (_pos + i < _tokens.Count &&
               _tokens[_pos + i].Kind is TokenKind.Public or TokenKind.Private
                   or TokenKind.Protected or TokenKind.Internal or TokenKind.Static
                   or TokenKind.Sealed)
            i++;
        if (_pos + i >= _tokens.Count || !IsTypeToken(_tokens[_pos + i].Kind)) return false;
        i++; // skip type token
        // skip optional array suffix []
        if (_pos + i < _tokens.Count && _tokens[_pos + i].Kind == TokenKind.LBracket
            && _pos + i + 1 < _tokens.Count && _tokens[_pos + i + 1].Kind == TokenKind.RBracket)
            i += 2;
        if (_pos + i >= _tokens.Count || _tokens[_pos + i].Kind != TokenKind.Identifier)
            return false;
        i++;
        if (_pos + i >= _tokens.Count) return false;
        // ; or = → regular field;  { → auto-property
        return _tokens[_pos + i].Kind is TokenKind.Semicolon or TokenKind.Eq or TokenKind.LBrace;
    }

    /// Skips an auto-property accessor body: `{ get; set; }` or `{ get; }` etc.
    private void SkipAutoPropBody()
    {
        Expect(TokenKind.LBrace);
        int depth = 1;
        while (!Check(TokenKind.Eof) && depth > 0)
        {
            if (Check(TokenKind.LBrace)) depth++;
            else if (Check(TokenKind.RBrace)) { depth--; if (depth == 0) break; }
            Advance();
        }
        Expect(TokenKind.RBrace);
    }

    // ── Function declaration ──────────────────────────────────────────────────

    private FunctionDecl ParseFunctionDecl(Visibility defaultVis)
    {
        var start = Current.Span;
        var vis = ParseVisibility(defaultVis);
        var (isStatic, isVirtual, isOverride) = ParseNonVisibilityModifiers();

        // Constructor pattern: Ident( — no explicit return type
        TypeExpr returnType;
        string   name;
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.LParen)
        {
            returnType = new VoidType(Current.Span);
            name       = Advance().Text;
        }
        else
        {
            returnType = ParseTypeExpr();
            name       = Expect(TokenKind.Identifier).Text;
        }

        Expect(TokenKind.LParen);
        var parms = new List<Param>();
        while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
        {
            var pSpan = Current.Span;
            var pType = ParseTypeExpr();
            var pName = Expect(TokenKind.Identifier).Text;
            parms.Add(new Param(pName, pType, pSpan));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen);

        var body = ParseBlock();
        return new FunctionDecl(name, parms, returnType, body, vis, isStatic, isVirtual, isOverride, start);
    }

    // ── Type expressions ──────────────────────────────────────────────────────

    internal TypeExpr ParseTypeExpr()
    {
        var span = Current.Span;

        string name = Current.Kind switch
        {
            TokenKind.Void   => Advance().Text,
            TokenKind.String => Advance().Text,
            TokenKind.Int    => Advance().Text,
            TokenKind.Long   => Advance().Text,
            TokenKind.Short  => Advance().Text,
            TokenKind.Double => Advance().Text,
            TokenKind.Float  => Advance().Text,
            TokenKind.Byte   => Advance().Text,
            TokenKind.Uint   => Advance().Text,
            TokenKind.Ulong  => Advance().Text,
            TokenKind.Ushort => Advance().Text,
            TokenKind.Sbyte  => Advance().Text,
            TokenKind.Object => Advance().Text,
            TokenKind.Bool   => Advance().Text,
            TokenKind.Char   => Advance().Text,
            TokenKind.I8 or TokenKind.I16 or TokenKind.I32 or TokenKind.I64 or
            TokenKind.U8 or TokenKind.U16 or TokenKind.U32 or TokenKind.U64 or
            TokenKind.F32 or TokenKind.F64 => Advance().Text,
            TokenKind.Identifier => Advance().Text,
            _ => throw new ParseException($"expected type name, got `{Current.Text}`", span)
        };

        TypeExpr ty = name == "void" ? new VoidType(span) : new NamedType(name, span);

        // T[] array type
        if (Check(TokenKind.LBracket) && Peek().Kind == TokenKind.RBracket)
        {
            Advance(); Advance();
            ty = new ArrayType(ty, span);
        }
        // T<U, V> generic — skip type args (not tracked in AST for Phase 1)
        else if (Check(TokenKind.Lt))
        {
            Advance(); int depth = 1;
            while (!Check(TokenKind.Eof) && depth > 0)
            {
                if (Check(TokenKind.Lt)) depth++;
                if (Check(TokenKind.Gt)) depth--;
                Advance();
            }
        }

        // T? option type
        if (Match(TokenKind.Question))
            ty = new OptionType(ty, span);

        return ty;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private string ParseQualifiedName()
    {
        var sb = new StringBuilder(Expect(TokenKind.Identifier).Text);
        while (Match(TokenKind.Dot))
            sb.Append('.').Append(Expect(TokenKind.Identifier).Text);
        return sb.ToString();
    }

    private void SkipAttribute()
    {
        Expect(TokenKind.LBracket);
        int depth = 1;
        while (!Check(TokenKind.Eof) && depth > 0)
        {
            if (Check(TokenKind.LBracket)) depth++;
            if (Check(TokenKind.RBracket)) depth--;
            Advance();
        }
    }
}
