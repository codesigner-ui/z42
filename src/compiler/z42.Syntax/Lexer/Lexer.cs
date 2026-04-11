using Z42.Core.Text;

namespace Z42.Syntax.Lexer;

/// <summary>
/// Hand-written lexer for z42 source code (Phase 1: C# syntax).
/// Produces a flat list of tokens from a source string.
///
/// Static token metadata (keywords, symbols) is in TokenDefs.cs.
/// Combinator-based rules (numbers, strings) are in LexRules.cs.
/// This class is a generic execution engine with no business logic.
/// </summary>
public sealed class Lexer(string source, string fileName = "<unknown>")
{
    private int _pos;
    private int _line = 1;
    private int _col  = 1;

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

    // ── Token dispatch ───────────────────────────────────────────────────────

    private Token NextToken()
    {
        int startPos = _pos, startLine = _line, startCol = _col;
        char c = source[_pos];

        // 1. String / char literals — matched by prefix (longest first)
        if (TryMatchStringRule(out var strRule))
            return LexStringBody(strRule!, startPos, startLine, startCol);

        // 2. Numeric literals — first digit triggers rule table
        if (char.IsDigit(c))
            return LexNumber(startPos, startLine, startCol);

        // 3. Identifier / keyword
        if (char.IsLetter(c) || c == '_')
            return LexIdentOrKeyword(startPos, startLine, startCol);

        // 4. Symbols — longest-match from SymbolRules
        if (TryMatchSymbol(out var symRule))
        {
            for (int i = 0; i < symRule!.Text.Length; i++) Advance();
            return new Token(symRule.Kind, symRule.Text, MakeSpan(startPos, startLine, startCol));
        }

        // Unknown character
        Advance();
        return new Token(TokenKind.Error_Unknown, source[startPos.._pos],
            MakeSpan(startPos, startLine, startCol));
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

    // ── String / char lexer ──────────────────────────────────────────────────

    private bool TryMatchStringRule(out LexRules.StringRule? rule)
    {
        foreach (var r in LexRules.StringRules)
        {
            if (_pos + r.Prefix.Length > source.Length) continue;
            if (source.AsSpan(_pos, r.Prefix.Length).SequenceEqual(r.Prefix.AsSpan()))
            { rule = r; return true; }
        }
        rule = null;
        return false;
    }

    private Token LexStringBody(
        LexRules.StringRule rule, int startPos, int startLine, int startCol)
    {
        // Advance past the prefix ("\"", "$\"", or "'")
        for (int i = 0; i < rule.Prefix.Length; i++) Advance();

        if (rule.IsChar)
        {
            // Char literal: ' (\\)? char '
            if (_pos < source.Length && source[_pos] == '\\') Advance(); // escape
            if (_pos < source.Length) Advance();                          // the char
            if (_pos < source.Length && source[_pos] == '\'') Advance(); // closing '
        }
        else if (rule.IsInterpolated)
        {
            // Interpolated string: $"...{expr}..." — track brace nesting
            int depth = 0;
            while (_pos < source.Length)
            {
                char c = source[_pos];
                if (c == '{') { depth++; Advance(); continue; }
                if (c == '}' && depth > 0) { depth--; Advance(); continue; }
                if (c == '"' && depth == 0) { Advance(); break; }
                if (c == '\\') Advance(); // skip escape char
                Advance();
            }
        }
        else
        {
            // Regular string: "..."
            while (_pos < source.Length && source[_pos] != '"')
            {
                if (source[_pos] == '\\') Advance(); // skip escape
                if (_pos < source.Length) Advance();
            }
            if (_pos < source.Length) Advance(); // closing "
        }

        return new Token(rule.Kind, source[startPos.._pos],
            MakeSpan(startPos, startLine, startCol));
    }

    // ── Numeric lexer ────────────────────────────────────────────────────────

    private Token LexNumber(int startPos, int startLine, int startCol)
    {
        foreach (var rule in LexRules.NumericRules)
        {
            var end = rule.Rule(source, _pos);
            if (end == null) continue;

            // Advance the lexer position to where the rule matched
            while (_pos < end.Value) Advance();
            SkipNumericSuffixes();
            return new Token(rule.Kind, source[startPos.._pos],
                MakeSpan(startPos, startLine, startCol));
        }
        // Should never reach here — s_decDigits always matches at least one digit
        throw new InvalidOperationException("LexNumber: no rule matched");
    }

    private void SkipNumericSuffixes()
    {
        while (_pos < source.Length && TokenDefs.NumericSuffixes.Contains(source[_pos]))
            Advance();
    }

    // ── Identifier / keyword lexer ───────────────────────────────────────────

    private Token LexIdentOrKeyword(int startPos, int startLine, int startCol)
    {
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            Advance();
        string text = source[startPos.._pos];
        var kind = TokenDefs.Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Identifier;
        // _ is a special discard pattern
        if (text == "_") kind = TokenKind.Underscore;
        return new Token(kind, text, MakeSpan(startPos, startLine, startCol));
    }

    // ── Symbol lexer (longest-match) ─────────────────────────────────────────

    private bool TryMatchSymbol(out TokenDefs.SymbolRule? rule)
    {
        if (!TokenDefs.SymbolIndex.TryGetValue(source[_pos], out var candidates))
        { rule = null; return false; }

        foreach (var r in candidates)
        {
            if (_pos + r.Text.Length > source.Length) continue;
            if (source.AsSpan(_pos, r.Text.Length).SequenceEqual(r.Text.AsSpan()))
            { rule = r; return true; }
        }
        rule = null;
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Span MakeSpan(int startPos, int startLine, int startCol) =>
        new(startPos, _pos, startLine, startCol, fileName);

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
