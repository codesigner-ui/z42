namespace Z42.Compiler.Lexer;

/// <summary>
/// Hand-written lexer for z42 source code (Phase 1: C# syntax).
/// Produces a flat list of tokens from a source string.
/// </summary>
public sealed class Lexer(string source, string fileName = "<unknown>")
{
    private int _pos;
    private int _line = 1;
    private int _col  = 1;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        // C# keywords
        ["namespace"] = TokenKind.Namespace,
        ["using"]     = TokenKind.Using,
        ["var"]       = TokenKind.Var,
        ["static"]    = TokenKind.Static,
        ["public"]    = TokenKind.Public,
        ["private"]   = TokenKind.Private,
        ["protected"] = TokenKind.Protected,
        ["internal"]  = TokenKind.Internal,
        ["class"]     = TokenKind.Class,
        ["struct"]    = TokenKind.Struct,
        ["enum"]      = TokenKind.Enum,
        ["interface"] = TokenKind.Interface,
        ["record"]    = TokenKind.Record,
        ["abstract"]  = TokenKind.Abstract,
        ["sealed"]    = TokenKind.Sealed,
        ["override"]  = TokenKind.Override,
        ["virtual"]   = TokenKind.Virtual,
        ["new"]       = TokenKind.New,
        ["async"]     = TokenKind.Async,
        ["await"]     = TokenKind.Await,
        ["return"]    = TokenKind.Return,
        ["if"]        = TokenKind.If,
        ["else"]      = TokenKind.Else,
        ["while"]     = TokenKind.While,
        ["for"]       = TokenKind.For,
        ["foreach"]   = TokenKind.Foreach,
        ["in"]        = TokenKind.In,
        ["do"]        = TokenKind.Do,
        ["break"]     = TokenKind.Break,
        ["continue"]  = TokenKind.Continue,
        ["switch"]    = TokenKind.Switch,
        ["case"]      = TokenKind.Case,
        ["default"]   = TokenKind.Default,
        ["try"]       = TokenKind.Try,
        ["catch"]     = TokenKind.Catch,
        ["finally"]   = TokenKind.Finally,
        ["throw"]     = TokenKind.Throw,
        ["typeof"]    = TokenKind.Typeof,
        ["is"]        = TokenKind.Is,
        ["as"]        = TokenKind.As,
        ["null"]      = TokenKind.Null,
        ["true"]      = TokenKind.True,
        ["false"]     = TokenKind.False,
        // C# type names
        ["string"]    = TokenKind.String,
        ["int"]       = TokenKind.Int,
        ["long"]      = TokenKind.Long,
        ["short"]     = TokenKind.Short,
        ["double"]    = TokenKind.Double,
        ["float"]     = TokenKind.Float,
        ["byte"]      = TokenKind.Byte,
        ["uint"]      = TokenKind.Uint,
        ["ulong"]     = TokenKind.Ulong,
        ["ushort"]    = TokenKind.Ushort,
        ["sbyte"]     = TokenKind.Sbyte,
        ["object"]    = TokenKind.Object,
        ["bool"]      = TokenKind.Bool,
        ["char"]      = TokenKind.Char,
        ["void"]      = TokenKind.Void,
        // Explicit-size aliases
        ["i8"]        = TokenKind.I8,
        ["i16"]       = TokenKind.I16,
        ["i32"]       = TokenKind.I32,
        ["i64"]       = TokenKind.I64,
        ["u8"]        = TokenKind.U8,
        ["u16"]       = TokenKind.U16,
        ["u32"]       = TokenKind.U32,
        ["u64"]       = TokenKind.U64,
        ["f32"]       = TokenKind.F32,
        ["f64"]       = TokenKind.F64,
        // Legacy z42 keywords
        ["fn"]        = TokenKind.Fn,
        ["let"]       = TokenKind.Let,
        ["mut"]       = TokenKind.Mut,
        ["trait"]     = TokenKind.Trait,
        ["impl"]      = TokenKind.Impl,
        ["use"]       = TokenKind.Use,
        ["module"]    = TokenKind.Module,
        ["spawn"]     = TokenKind.Spawn,
        ["none"]      = TokenKind.None,
    };

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (_pos < source.Length)
        {
            SkipWhitespaceAndComments();
            if (_pos >= source.Length) break;
            tokens.Add(NextToken());
        }
        tokens.Add(Token.Eof(_pos, fileName));
        return tokens;
    }

    // ── Whitespace & comments ────────────────────────────────────────────────

    private void SkipWhitespaceAndComments()
    {
        while (_pos < source.Length)
        {
            // Line comment
            if (_pos + 1 < source.Length && source[_pos] == '/' && source[_pos + 1] == '/')
            {
                while (_pos < source.Length && source[_pos] != '\n') Advance();
                continue;
            }
            // Block comment
            if (_pos + 1 < source.Length && source[_pos] == '/' && source[_pos + 1] == '*')
            {
                Advance(); Advance(); // consume /*
                while (_pos + 1 < source.Length &&
                       !(source[_pos] == '*' && source[_pos + 1] == '/'))
                    Advance();
                if (_pos + 1 < source.Length) { Advance(); Advance(); } // consume */
                continue;
            }
            if (source[_pos] is ' ' or '\t' or '\r' or '\n') { Advance(); continue; }
            break;
        }
    }

    // ── Token dispatch ───────────────────────────────────────────────────────

    private Token NextToken()
    {
        int startPos = _pos, startLine = _line, startCol = _col;
        char c = source[_pos];

        // Interpolated string: $"..."
        if (c == '$' && _pos + 1 < source.Length && source[_pos + 1] == '"')
        {
            return LexInterpolatedString(startPos, startLine, startCol);
        }

        // Regular string
        if (c == '"') return LexString(startPos, startLine, startCol);

        // Char literal
        if (c == '\'') return LexChar(startPos, startLine, startCol);

        // Number
        if (char.IsDigit(c)) return LexNumber(startPos, startLine, startCol);

        // Identifier / keyword
        if (char.IsLetter(c) || c == '_') return LexIdentOrKeyword(startPos, startLine, startCol);

        // Symbols
        Advance();
        var kind = c switch
        {
            '(' => TokenKind.LParen,
            ')' => TokenKind.RParen,
            '{' => TokenKind.LBrace,
            '}' => TokenKind.RBrace,
            '[' => TokenKind.LBracket,
            ']' => TokenKind.RBracket,
            ',' => TokenKind.Comma,
            '.' => Peek('.') ? Eat(TokenKind.DotDot) : TokenKind.Dot,
            ':' => Peek(':') ? Eat(TokenKind.ColonColon) : TokenKind.Colon,
            ';' => TokenKind.Semicolon,
            '?' => Peek('?') ? Eat(TokenKind.QuestionQuestion) : TokenKind.Question,
            '~' => TokenKind.Tilde,
            '^' => Peek('=') ? Eat(TokenKind.CaretEq) : TokenKind.Caret,
            '#' => TokenKind.Hash,
            '+' => Peek('+') ? Eat(TokenKind.PlusPlus) : Peek('=') ? Eat(TokenKind.PlusEq) : TokenKind.Plus,
            '-' => Peek('-') ? Eat(TokenKind.MinusMinus) : Peek('>') ? Eat(TokenKind.Arrow) : Peek('=') ? Eat(TokenKind.MinusEq) : TokenKind.Minus,
            '*' => Peek('=') ? Eat(TokenKind.StarEq)    : TokenKind.Star,
            '/' => Peek('=') ? Eat(TokenKind.SlashEq)   : TokenKind.Slash,
            '%' => Peek('=') ? Eat(TokenKind.PercentEq) : TokenKind.Percent,
            '=' => Peek('=') ? Eat(TokenKind.EqEq) : Peek('>') ? Eat(TokenKind.FatArrow) : TokenKind.Eq,
            '!' => Peek('=') ? Eat(TokenKind.BangEq)    : TokenKind.Bang,
            '<' => Peek('<') ? Eat(TokenKind.LtLt) : Peek('=') ? Eat(TokenKind.LtEq) : TokenKind.Lt,
            '>' => Peek('>') ? Eat(TokenKind.GtGt) : Peek('=') ? Eat(TokenKind.GtEq) : TokenKind.Gt,
            '&' => Peek('&') ? Eat(TokenKind.AmpAmp) : Peek('=') ? Eat(TokenKind.AmpEq) : TokenKind.Ampersand,
            '|' => Peek('|') ? Eat(TokenKind.PipePipe) : Peek('=') ? Eat(TokenKind.PipeEq) : TokenKind.Pipe,
            _   => TokenKind.Error_Unknown,
        };
        return new Token(kind, source[startPos.._pos], MakeSpan(startPos, startLine, startCol));
    }

    // ── Specific lexers ──────────────────────────────────────────────────────

    private Token LexInterpolatedString(int startPos, int startLine, int startCol)
    {
        Advance(); // $
        Advance(); // "
        int depth = 0;
        while (_pos < source.Length)
        {
            char c = source[_pos];
            if (c == '{') { depth++; Advance(); continue; }
            if (c == '}' && depth > 0) { depth--; Advance(); continue; }
            if (c == '"' && depth == 0) { Advance(); break; }
            if (c == '\\') Advance(); // skip escape
            Advance();
        }
        return new Token(TokenKind.InterpolatedStringLiteral,
            source[startPos.._pos],
            MakeSpan(startPos, startLine, startCol));
    }

    private Token LexString(int startPos, int startLine, int startCol)
    {
        Advance(); // opening "
        while (_pos < source.Length && source[_pos] != '"')
        {
            if (source[_pos] == '\\') Advance(); // skip escape
            if (_pos < source.Length) Advance();
        }
        if (_pos < source.Length) Advance(); // closing "
        return new Token(TokenKind.StringLiteral,
            source[startPos.._pos],
            MakeSpan(startPos, startLine, startCol));
    }

    private Token LexChar(int startPos, int startLine, int startCol)
    {
        Advance(); // opening '
        if (_pos < source.Length && source[_pos] == '\\') Advance();
        if (_pos < source.Length) Advance(); // char
        if (_pos < source.Length && source[_pos] == '\'') Advance(); // closing '
        return new Token(TokenKind.CharLiteral,
            source[startPos.._pos],
            MakeSpan(startPos, startLine, startCol));
    }

    private Token LexNumber(int startPos, int startLine, int startCol)
    {
        // Hex / binary prefix
        if (source[_pos] == '0' && _pos + 1 < source.Length)
        {
            if (source[_pos + 1] == 'x' || source[_pos + 1] == 'X')
            {
                Advance(); Advance(); // 0x
                while (_pos < source.Length && (char.IsAsciiHexDigit(source[_pos]) || source[_pos] == '_')) Advance();
                SkipNumericSuffixes();
                return new Token(TokenKind.IntLiteral, source[startPos.._pos], MakeSpan(startPos, startLine, startCol));
            }
            if (source[_pos + 1] == 'b' || source[_pos + 1] == 'B')
            {
                Advance(); Advance(); // 0b
                while (_pos < source.Length && (source[_pos] == '0' || source[_pos] == '1' || source[_pos] == '_')) Advance();
                SkipNumericSuffixes();
                return new Token(TokenKind.IntLiteral, source[startPos.._pos], MakeSpan(startPos, startLine, startCol));
            }
        }

        // Decimal digits (with _ separators)
        while (_pos < source.Length && (char.IsDigit(source[_pos]) || source[_pos] == '_')) Advance();

        bool isFloat = false;
        if (_pos < source.Length && source[_pos] == '.'
            && _pos + 1 < source.Length && char.IsDigit(source[_pos + 1]))
        {
            isFloat = true;
            Advance(); // .
            while (_pos < source.Length && (char.IsDigit(source[_pos]) || source[_pos] == '_')) Advance();
        }
        // Exponent
        if (_pos < source.Length && (source[_pos] == 'e' || source[_pos] == 'E'))
        {
            isFloat = true;
            Advance();
            if (_pos < source.Length && (source[_pos] == '+' || source[_pos] == '-')) Advance();
            while (_pos < source.Length && char.IsDigit(source[_pos])) Advance();
        }

        SkipNumericSuffixes();
        return new Token(isFloat ? TokenKind.FloatLiteral : TokenKind.IntLiteral,
            source[startPos.._pos],
            MakeSpan(startPos, startLine, startCol));
    }

    private void SkipNumericSuffixes()
    {
        // L, u, U, f, d, m, uL, ul, LU, etc.
        while (_pos < source.Length && (source[_pos] is 'L' or 'l' or 'u' or 'U' or 'f' or 'F' or 'd' or 'D' or 'm' or 'M'))
            Advance();
    }

    private Token LexIdentOrKeyword(int startPos, int startLine, int startCol)
    {
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            Advance();
        string text = source[startPos.._pos];
        var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
        // _ is a special discard pattern
        if (text == "_") kind = TokenKind.Underscore;
        return new Token(kind, text, MakeSpan(startPos, startLine, startCol));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Span MakeSpan(int startPos, int startLine, int startCol) =>
        new(startPos, _pos, startLine, startCol, fileName);

    private bool Peek(char expected) =>
        _pos < source.Length && source[_pos] == expected;

    private TokenKind Eat(TokenKind kind) { Advance(); return kind; }

    private void Advance()
    {
        if (_pos < source.Length)
        {
            if (source[_pos] == '\n') { _line++; _col = 1; }
            else _col++;
            _pos++;
        }
    }
}
