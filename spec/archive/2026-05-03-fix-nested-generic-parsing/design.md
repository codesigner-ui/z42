# Design: 嵌套 generic `>>` 拆分

## Architecture

```
Lexer 不变 ────────► emits GtGt as single token
                         │
                         ▼
TypeParser.Parse() ──► ConsumeClosingGt() helper
                         ├─ if current = Gt   → consume, advance, return
                         └─ if current = GtGt → splice: pretend current = Gt,
                                                replace cursor's current
                                                with synthetic Gt token,
                                                return (do NOT advance)
                                                — outer level will then see Gt
```

## Decisions

### Decision 1: parser-side 拆分（不引入 lexer mode）
**问题：** 如何把 `>>` 拆成两个 `>`？
**选项：**
- A. **Lexer mode**：parser 通知 lexer 当前在 type-arg 上下文，lexer 不合并 `>>`
- B. **Parser-side splice**：lexer 仍 emit `GtGt`；parser 在期望关闭点检查 `GtGt`，原地"消费一半"，把剩半留给外层

**决定：选 B。** 原因：
- A 引入 lexer-parser 状态耦合，破坏 lexer 无状态约定；类型上下文进入/退出的边界很多（field / param / return / expr / type-arg），易遗漏
- B 改动局部、无副作用；C# Roslyn 实际就是 B（`SyntaxToken.SplitGreaterThanGreaterThan`）
- B 不影响其他用到 `>>`（shift-right）的语境，因为 ShiftExpression 只在 expression 上下文，此处不会去 ConsumeClosingGt

### Decision 2: extraClose flag 线程穿透（取代原 token rewrite）
**问题：** `TokenCursor` 是 `readonly struct` + `IReadOnlyList<Token>`（[TokenCursor.cs:6-9](src/compiler/z42.Syntax/Parser/Core/TokenCursor.cs#L6-L9) 设计文档明示 lookahead/backtracking 依赖此 immutability），原方案"原地 rewrite token"违反架构。

**选项：**
- A. **rewrite cursor**（原方案）：违反 immutability — 放弃
- B. **`pendingGt` 计数器隐式状态**：parser-class 加可变字段；引入隐式状态
- C. **`extraClose` flag 通过返回值线程穿透**：每级 Parse 返回 `(value, remainder, extraClose)`；调用方根据 inner.extraClose 决定自己的 close 是否被 GtGt 吸收

**决定：选 C。** 原因：
- 与 immutable cursor + 函数式 ParseResult 风格一致
- 状态显式（在返回值里），无可变 state
- C# Roslyn 同款做法（`SyntaxKind.GreaterThanGreaterThanToken` 在 type-arg 上下文 split）

**算法**：
```
ParseInternal(cursor) → (value, remainder, extraClose)

generic 分支：
  loop_check: cursor 既非 Gt 也非 GtGt 也非 EOF 也未收到 inner.extraClose=true → 继续
  loop_body: 递归调用 ParseInternal；若 returned.extraClose → 退出循环（外层 close 已被吸收）
  close_check:
    if extraGtFromInner=true → my.extraClose=false（不动 cursor，inner 吸收了我的 close）
    else if Gt → advance(1)，my.extraClose=false
    else if GtGt → advance(1)，my.extraClose=true（吃 GtGt：用 1 个 Gt 关掉自己，剩下 1 个 Gt 给上层）
    else → no close found（保留现行行为：不 advance，让上层报错）
```

**追踪示例 `Foo<Bar<Baz<T>>>` (tokens: `Foo < Bar < Baz < T >> >`)**：
- Baz 解析 T → cursor 到 `>>`。close: GtGt → advance, my.extraClose=true。返回 cursor 到 `>`。
- Bar 收到 Baz returned.extraClose=true → loop 退出 → close: extraGtFromInner → my.extraClose=false。返回 cursor 仍在 `>`。
- Foo 收到 Bar returned.extraClose=false → close: cursor 在 `>` (Gt) → advance(1)。

**`Foo<Bar<T>>` (tokens: `Foo < Bar < T >>`)**：
- Bar 解析 T → cursor 到 `>>`。close: GtGt → advance, my.extraClose=true。
- Foo 收到 returned.extraClose=true → close: extraGtFromInner → my.extraClose=false。✓

**外接 API 不变**：`Parse(cursor) → ParseResult<TypeExpr>` 包装 ParseInternal，丢弃 extraClose（顶层 Parse 调用绝不该有 extraClose=true，否则是 parser 状态 bug）。

### Decision 3: depth-scan 站点统一 GtGt 计为 2
**问题：** 5 处 lookahead helper（`SkipGenericParams` / `IsFieldDecl` / `IsLocalFunctionDecl` / 索引器扫描 / 局部变量扫描）用 `int depth` 累加 Lt / 累减 Gt。这些站点不消费 token、只 peek，没有 extraClose 这个 cross-call 上下文。

**决定：** 每处的 switch / if-else 加：
```csharp
case TokenKind.GtGt: depth -= 2; break;
```
原因：scan 看到 `>>` 时确实跨越了 2 个 generic 关闭。简单、本地、零额外状态。

### Decision 4: 应用范围
**决定：** 所有解析 / 扫描 generic argument list 关闭点。具体：
- `TypeParser.Parse()` 关闭 `Foo<...>`（用 ParseInternal + extraClose）
- 5 个 depth-scan 站点（用 GtGt 计为 2）
- `ParseTypeParams` (`class Foo<T1, T2>`) **不需要修改** —— 类型参数列表只含 bare identifiers，无嵌套 generic 风险
- `[ShouldThrow<E>]` 属性单一类型参数 (TopLevelParser.Helpers.cs:295-301) **已显式拒绝嵌套**，不动

非 generic 关闭点的 `Gt`（如比较 `a > b`）**不动**。

## Implementation Notes

- 关键 API：`TypeParser.ConsumeClosingGt()`（private）
- cursor 改动：若 cursor.Current 是 readonly，加 `RewriteCurrent(Token replacement)` 内部方法
- 错误信息：保持 "expected '>'" 不要变成 "expected '>' or '>>'"（用户视角应一致）

## Testing Strategy

- 单元测试（C# `TypeParserTests.cs`）：4+ scenarios
  - `ParseField_NestedGeneric` — `Foo<Bar<T>> f;`
  - `ParseParam_NestedGeneric`
  - `ParseReturn_NestedGeneric`
  - `ParseExpr_NewArrayNestedGeneric` — `new Foo<Bar<T>>[n]`
  - `ParseType_TripleNested` — `Foo<Bar<Baz<T>>>`
  - `ParseType_SingleGeneric_StillWorks` —  regression
- 现有 D2a / 早期 generic 测试不破坏（dotnet test 全绿）
- D2b 阻塞用例：build `MulticastAction.z42` 含 `CompositeRef<Action<T>>[]` 字段不再 parse error
- VM 验证：`./scripts/test-vm.sh` 全绿
