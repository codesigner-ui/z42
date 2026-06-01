# Tasks: parser 错误消息列出 expected alternatives

> 状态：🟢 已完成 | 创建：2026-06-01 | 完成：2026-06-01 | 类型：refactor（diagnostic 增强，不改语言语义）
> 来源：[`docs/review.md`](../../../review.md) F5 #4

## 变更说明

ExprParser 的 Pratt Nud 表 miss 时，错误消息从单调的
`unexpected token `X` in expression` 升级为列出所有可作为表达式起始的 token，
对齐 Roslyn 的 `expected `(`, identifier, `default`, …, got `X`` 风格。

## 原因

review.md F5 #4 列出的 P1+ 编译器诊断改进项之一，提升用户体验、对齐
Roslyn。范围被刻意收窄到 Pratt Nud miss 一个 fork 点 ——

- 这是 expression 解析里**唯一**真正"我有 N 个合法 alternative，全都不匹配"的
  dispatch 点（NudTable.TryGetValue miss）
- `Combinators.Or` 看上去是另一处合法目标，但**实际未被任何 caller 使用**，
  改它零观察效果（详见 design 注）
- 其他 `throw new ParseException("expected X, got Y")` 都是单 alternative
  上下文（如 `MemberAccess` 后必须是 identifier），用不上 list

## 文档影响

- 无新语言行为 → 无 `docs/design/language/` 同步
- 无 IR / VM 变化
- 不需要在 `docs/design/` 落一篇新设计，diagnostic 措辞改进就近放 spec
- review.md F5 #4 完成后改为 ✅

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Syntax/Parser/Core/Combinators.cs` | MODIFY | 加 `KindList(IEnumerable<TokenKind>)` + `LabelList(IEnumerable<string>)` 渲染辅助 |
| `src/compiler/z42.Syntax/Parser/ExprParser.cs` | MODIFY | Nud miss 走新辅助，列出全部 NudTable keys（按 feature gate 过滤）+ 字面量 / 类型关键字合并为 `literal` / `identifier` 类别标签 |
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY | **Scope 扩展（2026-06-01 实施期发现）**：`s_display` 加 operator token 条目（`+` `-` `*` `/` `%` `!` `~` `++` `--` `==` `!=` `<` `<=` `>` `>=` `<<` `>>` `&&` `||` `&` `\|` `^` `+=` `-=` `*=` `/=` `%=` `&=` `\|=` `^=` `??` `..` `::`），否则 `Display(Bang)` fallback 到 `"bang"` 等丑陋名（review.md F5 #4 的目标是 Roslyn 风格人类可读 token 名） |
| `src/compiler/z42.Tests/ParserTests.cs` | MODIFY | 加 1 个测试：bad token in expression position → 错误消息含 `identifier` / `literal` / `(` / `new` / `typeof` / `default` / `!` / `++` |
| `src/compiler/z42.Tests/Fixtures/errors/multi_error/expected_error.txt` | MODIFY | **Scope 扩展**：原 glob 断言 `*unexpected*\`;\`*` 改为 `*expected expression*got \`;\`*` 以匹配新消息格式 |

只读引用：
- `src/compiler/z42.Core/Features/LanguageFeatures.cs` — 过滤 disabled feature 的 nud entries

## 任务

- [x] 0.1 NEW `docs/spec/changes/add-parser-expected-list/tasks.md`
- [ ] 1.1 MODIFY `Combinators.cs` 加 `internal static string KindList(IEnumerable<TokenKind>)`
  - 单项 → `` `X` ``
  - 双项 → `` `X` or `Y` ``
  - 多项 → `` `A`, `B`, or `C` ``（Oxford comma；按 Display 字母序排）
  - 阈值：> 8 项时退化到 `expression-start token` 字面（避免 noise）
- [x] 1.2 MODIFY `ExprParser.cs:99` Nud miss 分支
  - 收集 `s_nudTable` 所有 key（已注册的）
  - 排除 `Feature` 字段非空且 `feat.IsEnabled(...) == false` 的项
  - 字面量 / 类型关键字合并为类别标签
  - 错误消息：`` expected expression (<list>), got `<actual>` ``
- [x] 1.3 MODIFY `ParserTests.cs` 加 1 个测试 case `UnknownTokenInExpr_ListsExpressionAlternatives()`
- [x] 1.S1 **Scope 扩展**：MODIFY `TokenDefs.cs` 加 operator Display 条目
- [x] 1.S2 **Scope 扩展**：MODIFY `multi_error/expected_error.txt` 改 glob 适配新消息
- [x] 1.4 VERIFY: `dotnet test src/compiler/z42.Tests` 全过；现有 ParserTests 不回归（1444 tests pass）
- [x] 1.5 VERIFY: `./scripts/test-all.sh` 全绿（6 stages, scope=full, stdlib 257 file 全过）
- [x] 1.6 归档 `docs/spec/changes/add-parser-expected-list/` → `docs/spec/archive/2026-06-01-add-parser-expected-list/`
- [x] 1.7 review.md F5 #4 标 ✅
- [x] 1.8 commit + push

## 备注

- `Combinators.Or<T>` 当前未被任何代码调用 → 不在本 spec 范围（增强它收益 = 0）
- 后续可能的 follow-up（独立 spec）：把直接 `throw new ParseException("expected X")` 的若干 fork 点（如
  `TopLevelParser` member-kind 派发）也改造，但每个都是单独的判断 + 单独的错误措辞，
  没有统一的 NudTable 可借用 → 收益 / 工作量比不及本 spec
