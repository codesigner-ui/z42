using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser.Core;

/// Result of a single parser invocation.
///
/// Success path — holds the parsed <typeparamref name="T"/> value and the cursor
///   position after consuming all matched tokens.
///
/// Failure path — holds an error message and the span where the parse stopped.
///   Failures are *soft* by default: callers can inspect <see cref="IsOk"/> and
///   try an alternative branch.  Promote to hard errors via <see cref="OrThrow"/>
///   or <see cref="Unwrap"/>.
internal readonly struct ParseResult<T>
{
    private readonly T       _value;
    private readonly string? _error;

    private ParseResult(T value, TokenCursor remainder)
    {
        IsOk      = true;
        _value    = value;
        Remainder = remainder;
        _error    = null;
        ErrorSpan = default;
    }

    private ParseResult(string error, Span span)
    {
        IsOk      = false;
        _value    = default!;
        Remainder = default;
        _error    = error;
        ErrorSpan = span;
    }

    internal bool        IsOk      { get; }
    internal T           Value     => _value;
    internal TokenCursor Remainder { get; }
    internal string?     Error     => _error;
    internal Span        ErrorSpan { get; }

    // ── Factories ─────────────────────────────────────────────────────────────

    internal static ParseResult<T> Ok(T value, TokenCursor remainder) =>
        new(value, remainder);

    internal static ParseResult<T> Fail(TokenCursor at, string msg) =>
        new(msg, at.Current.Span);

    internal static ParseResult<T> Fail(string msg, Span span) =>
        new(msg, span);

    // ── Transforms ────────────────────────────────────────────────────────────

    /// Apply a function to the value (functor map); propagate failure unchanged.
    internal ParseResult<U> Map<U>(Func<T, U> f) =>
        IsOk ? ParseResult<U>.Ok(f(_value), Remainder)
             : ParseResult<U>.Fail(_error!, ErrorSpan);

    /// Chain a second parser using this result's remainder (monadic bind).
    internal ParseResult<U> FlatMap<U>(Func<T, TokenCursor, ParseResult<U>> f) =>
        IsOk ? f(_value, Remainder) : ParseResult<U>.Fail(_error!, ErrorSpan);

    /// Re-type a failure result (value type is irrelevant when IsOk is false).
    internal ParseResult<U> AsFailure<U>()
    {
        if (IsOk) throw new InvalidOperationException("AsFailure called on successful ParseResult");
        return ParseResult<U>.Fail(_error!, ErrorSpan);
    }

    // ── Unwrap helpers ────────────────────────────────────────────────────────

    /// Unwrap value or throw <see cref="ParseException"/> (hard syntax error).
    internal T OrThrow() =>
        IsOk ? _value : throw new ParseException(_error!, ErrorSpan);

    /// Unwrap value AND advance <paramref name="cursor"/> to the remainder position.
    /// The canonical pattern for sequential parsing:
    /// <code>var ty = TypeParser.Parse(cursor).Unwrap(ref cursor);</code>
    internal T Unwrap(ref TokenCursor cursor)
    {
        if (!IsOk) throw new ParseException(_error!, ErrorSpan);
        cursor = Remainder;
        return _value;
    }
}
