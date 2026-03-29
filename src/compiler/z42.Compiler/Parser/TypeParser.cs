using Z42.Compiler.Lexer;
using Z42.Compiler.Parser.Core;

namespace Z42.Compiler.Parser;

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
        // T<U, V> — generic type args (skipped, not tracked in AST for Phase 1)
        else if (cursor.Current.Kind == TokenKind.Lt)
        {
            cursor = cursor.Advance();
            int depth = 1;
            while (!cursor.IsEnd && depth > 0)
            {
                if (cursor.Current.Kind == TokenKind.Lt) depth++;
                if (cursor.Current.Kind == TokenKind.Gt) depth--;
                cursor = cursor.Advance();
            }
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
