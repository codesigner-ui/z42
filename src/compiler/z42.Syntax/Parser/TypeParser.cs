using Z42.Core.Text;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// Parses type expressions: primitive types, named types, T[], T&lt;U,V&gt;, T?
/// No feature gates — type syntax is always available.
internal static class TypeParser
{
    internal static bool IsTypeToken(TokenKind k) => TokenDefs.TypeKeywords.Contains(k);

    /// Parser<TypeExpr> — usable in combinator chains (P.SeparatedBy, P.Between, …)
    internal static readonly Parser<TypeExpr> TypeExpr = Parse;

    // ── Main parse function ───────────────────────────────────────────────────

    internal static ParseResult<TypeExpr> Parse(TokenCursor cursor)
    {
        var span = cursor.Current.Span;

        // Function type: `(T1, T2) -> R` — see docs/design/closure.md §3.2
        if (cursor.Current.Kind == TokenKind.LParen)
        {
            var funcResult = ParseFuncType(cursor);
            if (funcResult.IsOk) return funcResult;
            return ParseResult<TypeExpr>.Fail(cursor,
                $"expected type, got `{cursor.Current.Text}`");
        }

        if (!TokenDefs.TypeKeywords.Contains(cursor.Current.Kind))
            return ParseResult<TypeExpr>.Fail(cursor,
                $"expected type name, got `{cursor.Current.Text}`");

        var isVoid = cursor.Current.Kind == TokenKind.Void;
        var name   = cursor.Current.Text;
        cursor = cursor.Advance();

        TypeExpr ty = isVoid ? new VoidType(span) : new NamedType(name, span);

        // 2026-04-27 fix-generic-array-type-parsing：原版用 `if [] else if <>`
        // 互斥结构，遗漏 `T<U,V>[]` 复合形式（解析后 `[` `]` 残留 → 后续语句
        // 报"expected identifier got ["）。改为按顺序处理 `<...>` → `[]`，
        // 让两者可以叠加。`?` 仍在最后单独处理（合理，`T<U>?` / `T[]?` 都允许）。

        // T<U, V> — generic type arguments → GenericType node
        if (cursor.Current.Kind == TokenKind.Lt)
        {
            cursor = cursor.Advance(); // skip <
            var typeArgs = new List<TypeExpr>();
            while (cursor.Current.Kind != TokenKind.Gt && !cursor.IsEnd)
            {
                var argResult = Parse(cursor);
                if (!argResult.IsOk) break;
                typeArgs.Add(argResult.Value);
                cursor = argResult.Remainder;
                if (cursor.Current.Kind != TokenKind.Comma) break;
                cursor = cursor.Advance(); // skip ,
            }
            if (cursor.Current.Kind == TokenKind.Gt)
                cursor = cursor.Advance(); // skip >
            ty = new GenericType(name, typeArgs, span);
        }

        // T[] — array type（generic 之后也允许，以支持 `T<U,V>[]`）
        if (cursor.Current.Kind == TokenKind.LBracket
            && cursor.Peek(1).Kind == TokenKind.RBracket)
        {
            cursor = cursor.Advance(2);
            ty = new ArrayType(ty, span);
        }

        // T? — nullable / option type
        if (cursor.Current.Kind == TokenKind.Question)
        {
            cursor = cursor.Advance();
            ty = new OptionType(ty, span);
        }

        return ParseResult<TypeExpr>.Ok(ty, cursor);
    }

    /// Parse `(T1, T2) -> R`. Caller has confirmed `LParen` at cursor.
    /// Returns Fail if tokens after `)` are not `->` — caller handles that.
    private static ParseResult<TypeExpr> ParseFuncType(TokenCursor cursor)
    {
        var span = cursor.Current.Span;
        cursor = cursor.Advance(); // skip (

        var paramTypes = new List<TypeExpr>();
        if (cursor.Current.Kind != TokenKind.RParen)
        {
            var first = Parse(cursor);
            if (!first.IsOk) return ParseResult<TypeExpr>.Fail(cursor, first.Error!);
            paramTypes.Add(first.Value);
            cursor = first.Remainder;
            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                var next = Parse(cursor);
                if (!next.IsOk) return ParseResult<TypeExpr>.Fail(cursor, next.Error!);
                paramTypes.Add(next.Value);
                cursor = next.Remainder;
            }
        }
        if (cursor.Current.Kind != TokenKind.RParen)
            return ParseResult<TypeExpr>.Fail(cursor,
                $"expected `)` in function type, got `{cursor.Current.Text}`");
        cursor = cursor.Advance(); // skip )

        if (cursor.Current.Kind != TokenKind.Arrow)
            return ParseResult<TypeExpr>.Fail(cursor,
                $"expected `->` after `(...)` in function type, got `{cursor.Current.Text}`");
        cursor = cursor.Advance(); // skip ->

        var ret = Parse(cursor);
        if (!ret.IsOk) return ParseResult<TypeExpr>.Fail(cursor, ret.Error!);

        return ParseResult<TypeExpr>.Ok(
            new FuncType(paramTypes, ret.Value, span),
            ret.Remainder);
    }
}
