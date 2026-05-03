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

    /// Public entry: returns standard ParseResult, discards the internal
    /// `extraClose` bookkeeping flag used by nested-generic GtGt handling.
    internal static ParseResult<TypeExpr> Parse(TokenCursor cursor)
    {
        var r = ParseInternal(cursor);
        if (!r.Ok) return ParseResult<TypeExpr>.Fail(r.Remainder, r.Error!);
        return ParseResult<TypeExpr>.Ok(r.Value!, r.Remainder);
    }

    /// 2026-05-03 fix-nested-generic-parsing：内部 Parse 返回 `extraClose` flag
    /// 处理 nested generic 的 `>>` (GtGt) token 拆分。
    ///
    /// 算法：当 generic close 检查遇到 `GtGt` 时，consume 整个 token 并将
    /// `extraClose=true` 传给上层；上层接收到则视自己的 close 已被吸收
    /// （inner 的 GtGt 用 1 个 `>` 关掉自己 + 1 个 `>` 关掉上层）。
    /// 三层嵌套 `Foo<Bar<Baz<T>>>` (`>>` `>`)：Baz 吃 `>>` extraClose=true →
    /// Bar 吸收 → Foo 看到 `>` 走 Gt 路径。详见 design.md Decision 2。
    private static InternalResult ParseInternal(TokenCursor cursor)
    {
        var span = cursor.Current.Span;

        // Function type: `(T1, T2) -> R` — see docs/design/closure.md §3.2
        if (cursor.Current.Kind == TokenKind.LParen)
        {
            var funcResult = ParseFuncType(cursor);
            if (funcResult.IsOk)
                return InternalResult.Success(funcResult.Value, funcResult.Remainder);
            return InternalResult.Failure(cursor,
                $"expected type, got `{cursor.Current.Text}`");
        }

        if (!TokenDefs.TypeKeywords.Contains(cursor.Current.Kind))
            return InternalResult.Failure(cursor,
                $"expected type name, got `{cursor.Current.Text}`");

        var isVoid = cursor.Current.Kind == TokenKind.Void;
        var name   = cursor.Current.Text;
        cursor = cursor.Advance();

        TypeExpr ty = isVoid ? new VoidType(span) : new NamedType(name, span);
        bool extraClose = false;

        // 2026-04-27 fix-generic-array-type-parsing：原版用 `if [] else if <>`
        // 互斥结构，遗漏 `T<U,V>[]` 复合形式（解析后 `[` `]` 残留 → 后续语句
        // 报"expected identifier got ["）。改为按顺序处理 `<...>` → `[]`，
        // 让两者可以叠加。`?` 仍在最后单独处理（合理，`T<U>?` / `T[]?` 都允许）。

        // T<U, V> — generic type arguments → GenericType node
        if (cursor.Current.Kind == TokenKind.Lt)
        {
            cursor = cursor.Advance(); // skip <
            var typeArgs = new List<TypeExpr>();
            bool extraGtFromInner = false;
            while (cursor.Current.Kind != TokenKind.Gt
                && cursor.Current.Kind != TokenKind.GtGt
                && !cursor.IsEnd)
            {
                var argResult = ParseInternal(cursor);
                if (!argResult.Ok) break;
                typeArgs.Add(argResult.Value!);
                cursor = argResult.Remainder;
                if (argResult.ExtraClose)
                {
                    // Inner consumed a GtGt — its close absorbed mine too.
                    extraGtFromInner = true;
                    break;
                }
                if (cursor.Current.Kind != TokenKind.Comma) break;
                cursor = cursor.Advance(); // skip ,
            }
            // close-check 三路：
            // 1) inner 已用 GtGt 吸收我的 close → 不动 cursor，extraClose=false
            // 2) 单 `>` → 推进，extraClose=false
            // 3) `>>` → 推进，extraClose=true（上层吸收剩下那个 `>`）
            if (extraGtFromInner)
            {
                // already closed via inner's GtGt — nothing to do
            }
            else if (cursor.Current.Kind == TokenKind.Gt)
            {
                cursor = cursor.Advance();
            }
            else if (cursor.Current.Kind == TokenKind.GtGt)
            {
                cursor = cursor.Advance();
                extraClose = true;
            }
            // else: no close found — leave cursor for caller to detect
            ty = new GenericType(name, typeArgs, span);
        }

        // T[] — array type（generic 之后也允许，以支持 `T<U,V>[]`）。
        // extraClose=true 时跳过 —— GtGt 已 implicitly 关闭外层 generic，
        // 后续 `[]` `?` 属于外层而非本级（如 `Foo<Bar<int>>[]` 中 `[]` 附 Foo）。
        if (!extraClose
            && cursor.Current.Kind == TokenKind.LBracket
            && cursor.Peek(1).Kind == TokenKind.RBracket)
        {
            cursor = cursor.Advance(2);
            ty = new ArrayType(ty, span);
        }

        // T? — nullable / option type（extraClose 同上跳过）
        if (!extraClose && cursor.Current.Kind == TokenKind.Question)
        {
            cursor = cursor.Advance();
            ty = new OptionType(ty, span);
        }

        return InternalResult.Success(ty, cursor, extraClose);
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

    /// Internal parse outcome carrying the GtGt-split bookkeeping flag.
    /// `ExtraClose=true` means: this Parse's close consumed a `GtGt` token,
    /// covering both itself and its caller's close — caller must skip its own
    /// close advance step.
    private readonly record struct InternalResult(
        bool Ok,
        TypeExpr? Value,
        TokenCursor Remainder,
        string? Error,
        bool ExtraClose)
    {
        internal static InternalResult Success(TypeExpr v, TokenCursor r, bool extra = false) =>
            new(true, v, r, null, extra);
        internal static InternalResult Failure(TokenCursor at, string msg) =>
            new(false, null, at, msg, false);
    }
}
