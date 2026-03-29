namespace Z42.Compiler.Lexer.Core;

/// Lexer combinator: matches characters in a source string starting at `pos`.
/// Returns the new position after the match, or null if the rule did not match.
/// Mirrors Parser&lt;T&gt; from the parser layer but operates on raw characters.
internal delegate int? LexRule(string src, int pos);

/// Primitive and combinator library for building lexer rules.
/// All methods are pure functions — no mutable state.
internal static class LC
{
    // ── Primitives ────────────────────────────────────────────────────────────

    /// Match one character satisfying a predicate.
    internal static LexRule Char(Func<char, bool> pred) => (src, pos) =>
        pos < src.Length && pred(src[pos]) ? pos + 1 : null;

    /// Match one specific character.
    internal static LexRule Char(char c) => Char(ch => ch == c);

    /// Match one character from a set.
    internal static LexRule OneOf(params char[] chars) => Char(c =>
    {
        foreach (var ch in chars) if (c == ch) return true;
        return false;
    });

    /// Match an exact literal string (case-sensitive).
    internal static LexRule Lit(string s) => (src, pos) =>
        pos + s.Length <= src.Length
        && src.AsSpan(pos, s.Length).SequenceEqual(s.AsSpan())
            ? pos + s.Length : null;

    /// Match an exact literal string (case-insensitive, ASCII).
    internal static LexRule LitI(string s) => (src, pos) =>
        pos + s.Length <= src.Length
        && src.AsSpan(pos, s.Length).Equals(s.AsSpan(), StringComparison.OrdinalIgnoreCase)
            ? pos + s.Length : null;

    // ── Repetition ────────────────────────────────────────────────────────────

    /// Zero or more characters matching `pred`, with optional single-char separator.
    internal static LexRule Many(Func<char, bool> pred, char sep = '\0') => (src, pos) =>
    {
        while (pos < src.Length && pred(src[pos]))
        {
            pos++;
            // consume separator only if a digit follows (avoids trailing sep)
            if (sep != '\0' && pos < src.Length && src[pos] == sep
                && pos + 1 < src.Length && pred(src[pos + 1]))
                pos++;
        }
        return pos;
    };

    /// One or more characters matching `pred` (fails on zero matches).
    internal static LexRule Many1(Func<char, bool> pred, char sep = '\0')
    {
        var many = Many(pred, sep);
        return (src, pos) =>
        {
            var end = many(src, pos);
            return end > pos ? end : null;
        };
    }

    // ── Optional ──────────────────────────────────────────────────────────────

    /// Zero or one: always succeeds; returns original position if rule fails.
    internal static LexRule Opt(LexRule rule) => (src, pos) => rule(src, pos) ?? pos;

    // ── Sequence ──────────────────────────────────────────────────────────────

    /// All rules in order; fails if any rule fails.
    internal static LexRule Seq(params LexRule[] rules) => (src, pos) =>
    {
        foreach (var rule in rules)
        {
            var next = rule(src, pos);
            if (next == null) return null;
            pos = next.Value;
        }
        return pos;
    };

    // ── Alternation ───────────────────────────────────────────────────────────

    /// Try each rule in order; return first match.
    internal static LexRule Or(params LexRule[] rules) => (src, pos) =>
    {
        foreach (var rule in rules)
        {
            var result = rule(src, pos);
            if (result != null) return result;
        }
        return null;
    };

    // ── Composition helpers ───────────────────────────────────────────────────

    /// Run `rule` then optionally `tail`; succeeds only if `rule` matched.
    internal static LexRule Then(this LexRule rule, LexRule tail) =>
        Seq(rule, tail);

    /// Run `rule` and if it matched, optionally extend with `tail`.
    internal static LexRule ThenOpt(this LexRule rule, LexRule tail) =>
        rule.Then(Opt(tail));
}
