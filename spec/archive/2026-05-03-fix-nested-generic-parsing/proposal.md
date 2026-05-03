# Proposal: 修复嵌套 generic 类型 `>>` token 解析歧义

## Why

z42 lexer 把 `>>` 当作单一 `GtGt` token（shift-right），TypeParser 只识别单 `Gt` 闭合 generic argument list。导致**任何嵌套 generic 类型表达失败**：

- 字段：`Foo<Bar<T>> field;` 报 `expected identifier, got >>`
- 参数：`void m(Foo<Bar<T>> p)` 同上
- 表达式：`new Foo<Bar<T>>[n]` 报 `unexpected token [ in expression`

[TopLevelParser.Helpers.cs:494-497](src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs#L494-L497) 已有 known-limitation 注释。**这是 D2b ISubscription wrapper 体系**与未来所有 `Result<Option<T>>` / `Dictionary<string, List<int>>` / `Func<Action<T>>` 等组合类型的硬阻塞。

C# / Java / Roslyn 的标准做法：**parser 在 type-arg 上下文遇到 `GtGt` 时拆成两个 `Gt` 消费**。

## What Changes

- TypeParser 在期望 `Gt` 闭合 generic 时，若当前 token 是 `GtGt`，就消费"一半"（合成一个 `Gt`），把另一半留给外层
- `ConsumeClosingGt()` helper 封装该逻辑，覆盖：field 类型、parameter 类型、return 类型、`new T[n]` 中的元素类型、变量声明类型
- 移除 [TopLevelParser.Helpers.cs:494-497](src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs#L494-L497) 的 known-limitation 注释
- 不动 lexer（不引入 lexer mode 耦合）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/TypeParser.cs` | MODIFY | 加 `ParseInternal()` + extraClose flag 线程穿透；在 close 检查处理 GtGt |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `SkipGenericParams` (line 198) + `IsFieldDecl` 扫描 (line 507) 加 GtGt 计为 2；删除 known-limitation 注释 (494-497) |
| `src/compiler/z42.Syntax/Parser/StmtParser.cs` | MODIFY | 3 处 depth-scan (line 454/527/603) 加 GtGt 计为 2 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | 1 处 depth-scan (line 36) 加 GtGt 计为 2 |
| `src/compiler/z42.Tests/Syntax/TypeParserTests.cs` | NEW or MODIFY | 加 6+ 个 nested generic 解析测试 |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | z42.core 文件数 36 → 38（pre-existing failure，D2b 阶段 1 保留 wrapper 文件副作用，本 spec 收尾修复） |

**只读引用**：

- `src/compiler/z42.Syntax/Lexer/TokenDefs.cs:203` — 确认 `GtGt` 定义
- `src/compiler/z42.Syntax/Lexer/TokenKind.cs:52` — 确认 enum 值
- `src/libraries/z42.core/src/MulticastAction.z42` — D2b 用例验证

## Out of Scope

- 多字符 `>` token 的其他变体（`>=` `>>=` 当前不存在 / 不影响 generic 关闭）
- generic 类型实例化的 substitution / equality（属于另两个 spec）
- generic 类型系统本身的设计（这是 parse-only 修复）

## Open Questions

- [ ] `ConsumeClosingGt()` 是放在 TypeParser 还是抽到 ParseHelpers？倾向 TypeParser（该方法仅 type-arg 上下文需要）
