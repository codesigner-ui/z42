namespace Z42.Compiler.Lexer;

/// <summary>
/// Hand-written lexer for z42 source code.
/// Produces a flat list of tokens from a source string.
/// </summary>
public sealed class Lexer(string source, string fileName = "<unknown>")
{
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["fn"]     = TokenKind.Fn,
        ["let"]    = TokenKind.Let,
        ["mut"]    = TokenKind.Mut,
        ["return"] = TokenKind.Return,
        ["if"]     = TokenKind.If,
        ["else"]   = TokenKind.Else,
        ["while"]  = TokenKind.While,
        ["for"]    = TokenKind.For,
        ["in"]     = TokenKind.In,
        ["match"]  = TokenKind.Match,
        ["struct"] = TokenKind.Struct,
        ["enum"]   = TokenKind.Enum,
        ["trait"]  = TokenKind.Trait,
        ["impl"]   = TokenKind.Impl,
        ["use"]    = TokenKind.Use,
        ["module"] = TokenKind.Module,
        ["async"]  = TokenKind.Async,
        ["await"]  = TokenKind.Await,
        ["spawn"]  = TokenKind.Spawn,
        ["true"]   = TokenKind.True,
        ["false"]  = TokenKind.False,
        ["none"]   = TokenKind.None,
        ["error"]  = TokenKind.Error,
        ["i8"]     = TokenKind.I8,
        ["i16"]    = TokenKind.I16,
        ["i32"]    = TokenKind.I32,
        ["i64"]    = TokenKind.I64,
        ["u8"]     = TokenKind.U8,
        ["u16"]    = TokenKind.U16,
        ["u32"]    = TokenKind.U32,
        ["u64"]    = TokenKind.U64,
        ["f32"]    = TokenKind.F32,
        ["f64"]    = TokenKind.F64,
        ["bool"]   = TokenKind.Bool,
        ["char"]   = TokenKind.Char,
        ["str"]    = TokenKind.Str,
        ["void"]   = TokenKind.Void,
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
        tokens.Add(Token.Eof(_pos));
        return tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < source.Length)
        {
            if (source[_pos] == '/' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            {
                // Line comment
                while (_pos < source.Length && source[_pos] != '\n') _pos++;
                continue;
            }
            if (source[_pos] is ' ' or '\t' or '\r') { Advance(); continue; }
            if (source[_pos] == '\n') { Advance(); continue; }
            break;
        }
    }

    private Token NextToken()
    {
        int startPos = _pos, startLine = _line, startCol = _col;
        char c = source[_pos];

        // String literal
        if (c == '"') return LexString(startPos, startLine, startCol);

        // Char literal
        if (c == '\'') return LexChar(startPos, startLine, startCol);

        // Number
        if (char.IsDigit(c)) return LexNumber(startPos, startLine, startCol);

        // Identifier or keyword
        if (char.IsLetter(c) || c == '_') return LexIdentOrKeyword(startPos, startLine, startCol);

        // Symbols
        Advance();
        string text = source[startPos.._pos];

        TokenKind kind = c switch
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
            '?' => TokenKind.Question,
            '~' => TokenKind.Tilde,
            '^' => TokenKind.Caret,
            '#' => TokenKind.Hash,
            '+' => Peek('=') ? Eat(TokenKind.PlusEq) : TokenKind.Plus,
            '-' => Peek('>') ? Eat(TokenKind.Arrow) : Peek('=') ? Eat(TokenKind.MinusEq) : TokenKind.Minus,
            '*' => Peek('=') ? Eat(TokenKind.StarEq) : TokenKind.Star,
            '/' => Peek('=') ? Eat(TokenKind.SlashEq) : TokenKind.Slash,
            '%' => Peek('=') ? Eat(TokenKind.PercentEq) : TokenKind.Percent,
            '=' => Peek('=') ? Eat(TokenKind.EqEq) : Peek('>') ? Eat(TokenKind.FatArrow) : TokenKind.Eq,
            '!' => Peek('=') ? Eat(TokenKind.BangEq) : TokenKind.Bang,
            '<' => Peek('<') ? Eat(TokenKind.LtLt) : Peek('=') ? Eat(TokenKind.LtEq) : TokenKind.Lt,
            '>' => Peek('>') ? Eat(TokenKind.GtGt) : Peek('=') ? Eat(TokenKind.GtEq) : TokenKind.Gt,
            '&' => Peek('&') ? Eat(TokenKind.AmpAmp) : TokenKind.Ampersand,
            '|' => Peek('|') ? Eat(TokenKind.PipePipe) : TokenKind.Pipe,
            _ => TokenKind.Error_Unknown,
        };

        return new Token(kind, source[startPos.._pos], new Span(startPos, _pos, startLine, startCol));
    }

    private bool Peek(char expected) => _pos < source.Length && source[_pos] == expected;

    private TokenKind Eat(TokenKind kind) { Advance(); return kind; }

    private Token LexString(int startPos, int startLine, int startCol)
    {
        Advance(); // opening "
        while (_pos < source.Length && source[_pos] != '"')
        {
            if (source[_pos] == '\\') Advance(); // skip escape
            Advance();
        }
        if (_pos < source.Length) Advance(); // closing "
        return new Token(TokenKind.StringLiteral, source[startPos.._pos], new Span(startPos, _pos, startLine, startCol));
    }

    private Token LexChar(int startPos, int startLine, int startCol)
    {
        Advance(); // opening '
        if (_pos < source.Length && source[_pos] == '\\') Advance();
        if (_pos < source.Length) Advance(); // char
        if (_pos < source.Length && source[_pos] == '\'') Advance(); // closing '
        return new Token(TokenKind.CharLiteral, source[startPos.._pos], new Span(startPos, _pos, startLine, startCol));
    }

    private Token LexNumber(int startPos, int startLine, int startCol)
    {
        while (_pos < source.Length && char.IsDigit(source[_pos])) Advance();
        bool isFloat = false;
        if (_pos < source.Length && source[_pos] == '.' && _pos + 1 < source.Length && char.IsDigit(source[_pos + 1]))
        {
            isFloat = true;
            Advance();
            while (_pos < source.Length && char.IsDigit(source[_pos])) Advance();
        }
        return new Token(isFloat ? TokenKind.FloatLiteral : TokenKind.IntLiteral, source[startPos.._pos], new Span(startPos, _pos, startLine, startCol));
    }

    private Token LexIdentOrKeyword(int startPos, int startLine, int startCol)
    {
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_')) Advance();
        string text = source[startPos.._pos];
        var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
        return new Token(kind, text, new Span(startPos, _pos, startLine, startCol));
    }

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
