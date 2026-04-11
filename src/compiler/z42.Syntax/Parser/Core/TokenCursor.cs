using Z42.Core.Text;
using Z42.Syntax.Lexer;

namespace Z42.Syntax.Parser.Core;

/// Immutable cursor into a flat token stream.
/// All navigation returns a new cursor; the original is unchanged.
/// This makes lookahead, backtracking, and parallel parse attempts trivial to reason about —
/// particularly useful for top-level lookahead helpers (IsFieldDecl, IsClassDecl, …).
internal readonly struct TokenCursor
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly int _pos;

    private TokenCursor(IReadOnlyList<Token> tokens, int pos)
    {
        _tokens = tokens;
        _pos    = pos < 0 ? 0 : pos > tokens.Count ? tokens.Count : pos;
    }

    internal static TokenCursor From(IReadOnlyList<Token> tokens) => new(tokens, 0);

    // ── Navigation ────────────────────────────────────────────────────────────

    internal Token Current =>
        _pos < _tokens.Count ? _tokens[_pos] : Token.Eof(0);

    internal Token Peek(int offset = 1) =>
        _pos + offset < _tokens.Count ? _tokens[_pos + offset] : Token.Eof(0);

    internal TokenCursor Advance(int n = 1) => new(_tokens, _pos + n);

    internal bool IsEnd =>
        _pos >= _tokens.Count || _tokens[_pos].Kind == TokenKind.Eof;

    internal int Position => _pos;

    // ── Lookahead helper ──────────────────────────────────────────────────────

    /// Skip tokens while `pred` holds; return the resulting cursor.
    /// Used in top-level lookahead helpers to replace manual `_pos + i` arithmetic.
    internal TokenCursor SkipWhile(Func<Token, bool> pred)
    {
        var c = this;
        while (!c.IsEnd && pred(c.Current))
            c = c.Advance();
        return c;
    }
}
