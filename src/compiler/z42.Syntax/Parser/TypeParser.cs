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
        if (!TokenDefs.TypeKeywords.Contains(cursor.Current.Kind))
            return ParseResult<TypeExpr>.Fail(cursor,
                $"expected type name, got `{cursor.Current.Text}`");

        var isVoid = cursor.Current.Kind == TokenKind.Void;
        var name   = cursor.Current.Text;
        cursor = cursor.Advance();

        TypeExpr ty = isVoid ? new VoidType(span) : new NamedType(name, span);

        // T[] — array type
        if (cursor.Current.Kind == TokenKind.LBracket
            && cursor.Peek(1).Kind == TokenKind.RBracket)
        {
            cursor = cursor.Advance(2);
            ty = new ArrayType(ty, span);
        }
        // T<U, V> — generic type arguments → GenericType node
        else if (cursor.Current.Kind == TokenKind.Lt)
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

        // T? — nullable / option type
        if (cursor.Current.Kind == TokenKind.Question)
        {
            cursor = cursor.Advance();
            ty = new OptionType(ty, span);
        }

        return ParseResult<TypeExpr>.Ok(ty, cursor);
    }
}
