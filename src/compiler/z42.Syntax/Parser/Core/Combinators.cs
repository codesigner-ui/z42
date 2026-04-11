using Z42.Core.Text;
using Z42.Syntax.Lexer;
using static Z42.Syntax.Lexer.TokenDefs;

namespace Z42.Syntax.Parser.Core;

/// A parser: maps a token cursor to a parse result.
/// Modelled after the Datalust/Superpower delegate type, hand-written for z42 self-hosting.
internal delegate ParseResult<T> Parser<T>(TokenCursor cursor);

/// Combinator library for building parsers from smaller pieces.
/// All combinators are pure functions; no mutable state is used.
internal static class Combinators
{
    // ── Primitives ────────────────────────────────────────────────────────────

    /// Consume one token of exactly the given kind.
    internal static Parser<Token> Token(TokenKind kind) => cursor =>
        cursor.Current.Kind == kind
            ? ParseResult<Token>.Ok(cursor.Current, cursor.Advance())
            : ParseResult<Token>.Fail(cursor,
                $"expected `{KindDisplay(kind)}`, got `{cursor.Current.Text}`");

    /// Consume any identifier token.
    internal static readonly Parser<Token> Identifier = Token(TokenKind.Identifier);

    /// Succeed without consuming input, returning a constant value.
    internal static Parser<T> Return<T>(T value) =>
        cursor => ParseResult<T>.Ok(value, cursor);

    /// Always fail with the given message.
    internal static Parser<T> Fail<T>(string msg) =>
        cursor => ParseResult<T>.Fail(cursor, msg);

    // ── Transform ─────────────────────────────────────────────────────────────

    /// Apply a function to a successful result value (functor map).
    internal static Parser<U> Select<T, U>(this Parser<T> p, Func<T, U> f) =>
        cursor => p(cursor).Map(f);

    /// Monadic bind — enables C# LINQ query syntax over parsers.
    internal static Parser<V> SelectMany<T, U, V>(
        this Parser<T> p,
        Func<T, Parser<U>> next,
        Func<T, U, V> project) => cursor =>
    {
        var r1 = p(cursor);
        if (!r1.IsOk) return r1.AsFailure<V>();
        var r2 = next(r1.Value)(r1.Remainder);
        if (!r2.IsOk) return r2.AsFailure<V>();
        return ParseResult<V>.Ok(project(r1.Value, r2.Value), r2.Remainder);
    };

    // ── Alternation ───────────────────────────────────────────────────────────

    /// Try `a`; if it fails *without consuming input*, try `b` from the same position.
    internal static Parser<T> Or<T>(this Parser<T> a, Parser<T> b) => cursor =>
    {
        var r = a(cursor);
        return r.IsOk ? r : b(cursor);
    };

    // ── Sequencing ────────────────────────────────────────────────────────────

    /// Parse A then B; return a tuple of both results.
    internal static Parser<(A, B)> Then<A, B>(this Parser<A> a, Parser<B> b) => cursor =>
    {
        var ra = a(cursor);
        if (!ra.IsOk) return ra.AsFailure<(A, B)>();
        var rb = b(ra.Remainder);
        if (!rb.IsOk) return rb.AsFailure<(A, B)>();
        return ParseResult<(A, B)>.Ok((ra.Value, rb.Value), rb.Remainder);
    };

    /// Parse A then B; discard A's result, return B's value.
    internal static Parser<B> SkipThen<A, B>(this Parser<A> a, Parser<B> b) => cursor =>
    {
        var ra = a(cursor);
        if (!ra.IsOk) return ra.AsFailure<B>();
        return b(ra.Remainder);
    };

    /// Parse A then B; discard B's result, return A's value.
    internal static Parser<A> ThenSkip<A, B>(this Parser<A> a, Parser<B> b) => cursor =>
    {
        var ra = a(cursor);
        if (!ra.IsOk) return ra;
        var rb = b(ra.Remainder);
        if (!rb.IsOk) return rb.AsFailure<A>();
        return ParseResult<A>.Ok(ra.Value, rb.Remainder);
    };

    // ── Repetition ────────────────────────────────────────────────────────────

    /// Zero or more: collect results until the parser fails.
    internal static Parser<List<T>> Many<T>(this Parser<T> p) => cursor =>
    {
        var list = new List<T>();
        while (true)
        {
            var r = p(cursor);
            if (!r.IsOk) break;
            list.Add(r.Value);
            cursor = r.Remainder;
        }
        return ParseResult<List<T>>.Ok(list, cursor);
    };

    // ── Optional ──────────────────────────────────────────────────────────────

    /// Zero or one (reference types): returns null if the parser doesn't match.
    internal static Parser<T?> Optional<T>(this Parser<T> p) where T : class => cursor =>
    {
        var r = p(cursor);
        return r.IsOk
            ? ParseResult<T?>.Ok(r.Value, r.Remainder)
            : ParseResult<T?>.Ok(null, cursor);
    };

    // ── Delimited lists ───────────────────────────────────────────────────────

    /// `elem (sep elem)*` — returns all elements; trailing separator is accepted.
    internal static Parser<List<T>> SeparatedBy<T>(this Parser<T> p, TokenKind sep) => cursor =>
    {
        var list  = new List<T>();
        var first = p(cursor);
        if (!first.IsOk) return ParseResult<List<T>>.Ok(list, cursor);
        list.Add(first.Value);
        cursor = first.Remainder;
        while (cursor.Current.Kind == sep)
        {
            var afterSep = cursor.Advance();
            var next     = p(afterSep);
            if (!next.IsOk) break;   // trailing separator — stop without error
            list.Add(next.Value);
            cursor = next.Remainder;
        }
        return ParseResult<List<T>>.Ok(list, cursor);
    };

    /// `open p close` — consumes both delimiters, returns the inner value.
    internal static Parser<T> Between<T>(
        this Parser<T> p, TokenKind open, TokenKind close) => cursor =>
    {
        if (cursor.Current.Kind != open)
            return ParseResult<T>.Fail(cursor, $"expected `{KindDisplay(open)}`");
        cursor = cursor.Advance();
        var r = p(cursor);
        if (!r.IsOk) return r;
        cursor = r.Remainder;
        if (cursor.Current.Kind != close)
            return ParseResult<T>.Fail(cursor, $"expected `{KindDisplay(close)}`");
        return ParseResult<T>.Ok(r.Value, cursor.Advance());
    };

    // ── Lazy ──────────────────────────────────────────────────────────────────

    /// Defer parser construction — needed for mutually recursive parsers.
    internal static Parser<T> Lazy<T>(Func<Parser<T>> factory) =>
        cursor => factory()(cursor);

    // ── Display helper (used by error messages throughout the parser) ──────────

    internal static string KindDisplay(TokenKind k) => Display(k);
}
