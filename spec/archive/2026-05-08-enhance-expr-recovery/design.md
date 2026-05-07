# Design: Expression-level Parser Error Recovery

## Architecture

```
                          (existing)
caller -----▶ ExprParser.Parse(cursor, feat) -----▶ ParseResult<Expr>
                  │                                       │
                  └─ throw on internal ParseException ────┘
                                                       cursor.Unwrap → throw

                          (new — enabled when diags passed)
caller -----▶ ExprParser.Parse(cursor, feat, diags=bag) ──▶ ParseResult<Expr>
                  │                                              │
                  └─ try {                                        │
                       ParseInternal(cursor, feat)                │
                     }                                            │
                     catch (ParseException ex) {                  │
                       diags.Error(ex.Code, ex.Message, ex.Span)  │
                       cursor = SkipToExprBoundary(cursor)        │
                       return ParseResult.Ok(                     │
                         new ErrorExpr(ex.Message, ex.Span),      │
                         cursor)                                  │
                     } ───────────────────────────────────────────┘
                     ↑ Unwrap 看到 IsOk=true（携带 ErrorExpr），不抛
```

调用方只需 thread DiagnosticBag；不持 bag 时（test path）保持原 throw 语义。

## Decisions

### Decision 1: 顶层单层 catch vs 内部递归 recovery

**问题**: ExprParser 内部有 Pratt loop / nud table / led table 多层嵌套；每层都有 throw。是否每层都加 try/catch？

**选项**:
- A — 仅顶层 `Parse()` 入口 catch；内部 throw 冒泡到顶层
- B — 每个 nud/led handler 都 catch + 返回 ErrorExpr

**决定**: 选 A。原因:
1. 单层 catch 已经能让 `f(bad, ok)` 形态独立恢复（每个 arg 是独立 `Parse()` 调用）
2. 选项 B 工程量 5x，且容易留漏洞（哪些路径需要 catch 哪些不需要难统一）
3. review.md §3.7 评价"不可变 cursor + Error 节点"设计已经"更优雅"，不必激进重写
4. 真正需要"嵌套 expr 多错误"的场景（如复杂 LSP）出现时，独立 spec 推进

### Decision 2: ParseArgList 是否独立 catch

**问题**: `ParseArgList` 内部循环调 `Parse(cursor, feat)` 每个 arg；如果只有顶层 catch，单个 arg 失败会让 catch 在 Parse 顶层产生 ErrorExpr，arg 继续。无需 ArgList 自己 catch。

**决定**: ArgList **不需要**自己 catch；只需把 `diags` 参数 thread 给内部 `Parse()` 调用即可。

### Decision 3: SkipToExprBoundary 是否穿越嵌套括号

**问题**: 在 `f(bad, (inner)) outer` 中，bad 处 skip 应该停在第一个 `,` 还是穿到 `outer`？

**选项**:
- A — 平面 skip：遇到任意 sync token 即停
- B — bracket-aware skip：跟踪 `(`/`)` `[`/`]` `{`/`}` 嵌套深度，仅在外层 sync 停

**决定**: 选 A（简洁）。原因:
1. ArgList 循环本身有边界判定（看到 `)` 停），bracket-aware skip 只是"提前到位"
2. 嵌套表达式失败时，外层 caller（如 ArgList）的循环条件也会触发停止
3. 实测当前 z42 错误消息已经在合理位置定位，平面 skip 不会显著恶化诊断质量

### Decision 4: ErrorExpr 节点 vs 重新合成空 Expr

**问题**: 失败时返回什么节点？

**决定**: 用现有 `ErrorExpr(string Message, Span Span)`。原因:
1. 节点已在 AST 定义（line 344）
2. TypeChecker.Exprs.cs:256 已有防御 `case ErrorExpr` 路径（ICE-style throw 拒绝继续 codegen）
3. 不需要新增 AST node 类型

### Decision 5: Parser.ParseExpr() 公开 API 行为

**问题**: `Parser.ParseExpr()` 当前 `.OrThrow()`，给单测路径用。是否改？

**决定**: **不改**。Test 路径需要 fail-fast；保留 throw 行为符合预期。新 API 通过 `ExprParser.Parse(cursor, feat, diags)` 内部使用，不改公开 API。

## Implementation Notes

### `SkipToExprBoundary` 实现

```csharp
internal static TokenCursor SkipToExprBoundary(TokenCursor cursor)
{
    while (!cursor.IsEnd)
    {
        var k = cursor.Current.Kind;
        if (k is TokenKind.Comma
              or TokenKind.RParen
              or TokenKind.RBracket
              or TokenKind.RBrace
              or TokenKind.Semicolon)
            break;
        cursor = cursor.Advance();
    }
    return cursor;
}
```

放在 `ExprParser.cs` 私有 static helper（与 ExprParser 内 `Parse()` 同文件）。

### `ExprParser.Parse` 重载

```csharp
internal static ParseResult<Expr> Parse(
    TokenCursor cursor, LanguageFeatures feat,
    int minBp = 0, DiagnosticBag? diags = null)
{
    if (diags == null)
        return ParseInternal(cursor, feat, minBp);  // 现有逻辑

    try
    {
        return ParseInternal(cursor, feat, minBp);
    }
    catch (ParseException ex)
    {
        diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken,
                    ex.Message, ex.Span);
        var skipped = SkipToExprBoundary(cursor);
        return ParseResult<Expr>.Ok(new ErrorExpr(ex.Message, ex.Span), skipped);
    }
}

// 把现有 Parse 主体提取为 private ParseInternal（行为不变）
private static ParseResult<Expr> ParseInternal(
    TokenCursor cursor, LanguageFeatures feat, int minBp)
{
    /* 原 Parse 主体 */
}
```

### 调用方 thread DiagnosticBag

`StmtParser.cs` 已经在 ParseBlock 持有 `diags`；改 16 处调用：

```csharp
// 旧
init = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);

// 新（thread bag）
init = ExprParser.Parse(cursor, feat, diags: diags).Unwrap(ref cursor);
```

注意 `Parse` 第三参 `int minBp = 0`，第四参才是 `diags`。用 named arg `diags: diags` 明确避免位置歧义。

### ArgList 改造

```csharp
private static List<Expr> ParseArgList(
    ref TokenCursor cursor, TokenKind stop, LanguageFeatures feat,
    bool allowModifiers = false, DiagnosticBag? diags = null)  // 新增 diags
{
    var args = new List<Expr>();
    while (cursor.Current.Kind != stop && !cursor.IsEnd)
    {
        Expr arg = allowModifiers
            ? ParseCallArgWithOptionalModifier(ref cursor, feat, diags)
            : Parse(cursor, feat, diags: diags).Unwrap(ref cursor);
        args.Add(arg);
        if (cursor.Current.Kind != TokenKind.Comma) break;
        cursor = cursor.Advance();
    }
    return args;
}
```

## Testing Strategy

- 单元测试（C#）: `ParserRecoveryTests.cs` 5 个 case
  1. 单 expr error → 1 ErrorExpr + 1 diag
  2. 顺序两个 stmt 各含 expr error → 2 ErrorExpr + 2 diag
  3. `f(bad1, bad2, ok)` → 3 args 含 2 ErrorExpr + 2 diag
  4. 数组字面量 `[1, bad, 3]` → 3 elems 含 1 ErrorExpr + 1 diag
  5. 不传 bag 仍 throw（向后兼容）
- 现有 ParserTests / GoldenTests: 全 1104 case 不变（不传 bag 路径）
- VM golden: 不受影响（parser 错误从来不进 VM）
