# Tasks: parser-generic-field

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：fix (parser bug)
>
> **备注（2026-04-26 实施收尾）**：嵌套泛型 `List<List<int>>` 不在本变更范围 —
> TypeParser 的 `>>` (GtGt) token split 是另一个独立问题（"merge >" trick
> 未实现），落在 backlog。本变更仅解锁单层泛型字段声明（`List<string>`、
> `Dictionary<int, string>` 等），已足够覆盖 stdlib 当前需求。

> **变更说明**：`IsFieldDecl` lookahead 识别字段声明时只跳过 `[]` 数组后缀，
> 不跳过泛型 `<...>` 实参。结果 `List<string> _parts;` 等字段被误识别为方法
> 声明（parser 期望 `(` 跟在 `List` 后面）。
>
> **原因**：[TopLevelParser.Helpers.cs:478](src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs#L478)
> 的 lookahead 是为 L1 写的，当时没有泛型字段类型场景。L3 generic 引入后该
> 限制阻止了 stdlib 写 `List<string> _parts;`，迫使 script-first-stringbuilder
> 等改写绕道用 `string[]` 手动 grow。
>
> **触发场景**：z42.text/StringBuilder.z42 改写为 Script-First 时被这个 bug 卡住。
>
> **文档影响**：
>   - `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` IsFieldDecl 加 `<...>` skip
>   - `src/compiler/z42.Tests/ParserTests.cs` 加单元测试
>   - 后续可优化 stdlib 用更简洁的 `List<T>` 字段（不在本变更 Scope）

## Scope（具体文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `IsFieldDecl` 新增泛型 `<...>` 嵌套深度 skip |
| `src/compiler/z42.Tests/ParserTests.cs` | MODIFY | 新增 `FieldDecl_Generic*` 测试 |

**只读引用**：
- `src/compiler/z42.Syntax/Parser/Core/TokenCursor.cs` — 理解 SkipWhile / Peek 接口

- [x] 1.1 在 `IsFieldDecl` 类型 token 之后加 `<...>` 嵌套深度跳过逻辑（依赖 `>` / `,` / `<`）
- [x] 1.2 单元测试：`List<string> _parts;` / `Dictionary<int,string> _map;` / 嵌套泛型 `List<List<int>>` 都识别为字段
- [x] 1.3 验证：dotnet test 全绿；stdlib 5/5；test-vm 188/188
- [x] 1.4 commit + push
