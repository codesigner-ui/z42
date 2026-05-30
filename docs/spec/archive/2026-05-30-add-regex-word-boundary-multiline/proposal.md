# Proposal: 给 Std.Regex 加 `\b` / `\B` 与 `(?m)` multiline 模式

## Why

`scripts/` bash → z42 移植路径在 P2 撞到两个 regex 缺口
（memory `project_scripts_z42_port.md`）：

1. **`\b` / `\B` word boundary** — 词边界是 `grep -w` / 配置文件 token 抽取
   等场景的常用谓词；没有它，调用方只能用更脆弱的 `[^a-zA-Z0-9_]` 兜底，
   且会污染 captured group 的 start/end，让脚本算 offset 麻烦。
2. **`(?m)` multiline 模式** — 当前 `^`/`$` 锁死在整个输入串的首末。多行
   日志 / config 文件每行独立分析时，只能预先 `Split("\n")` 逐行调用
   `Regex`，丢失"换行后再继续 backtrack"的能力，也让 `FindAll` 不可用。

两个缺口都列在 `docs/design/stdlib/regex.md` 的 Deferred 段，但没有
spec；这次一起补齐。

不做会怎样：bash 脚本继续无法整段移植；regex.md 设计决策表里
"留 Deferred" 的两行始终悬空。

## What Changes

- `RegexNode` 新增两种 kind：`9 = WORD_BOUNDARY`、`10 = WORD_NON_BOUNDARY`
- `RegexParser.ParseEscape` 把 `\b` / `\B` 分别 emit 成上述节点
- `Regex.Compile` 的 inline-flag 前缀解析升级：
  - 当前只识别 `(?i)`；改为识别 `(?` + 任意 `[im]` 组合 + `)`
  - 新增 `_multiline` 字段
- 引擎 (`Regex.Match`) 新增两个 kind 的判定（零宽，不消耗字符）
- 引擎 `ANCHOR_START` (k=4) 在 `_multiline = true` 时，额外允许"前一
  字符是 `\n`"；`ANCHOR_END` (k=5) 额外允许"当前字符是 `\n`"
- 新增 unit tests：
  - `tests/regex_word_boundary.z42`
  - `tests/regex_multiline.z42`
- `docs/design/stdlib/regex.md` 同步：Decision 6/7 表行从"留 Deferred"
  改为"已落地"+ 引用本 spec；Deferred 段移除对应条目
- `docs/roadmap.md` Deferred Backlog Index 不动（这两项不在 backlog 表，
  只在 regex.md 内部）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.regex/src/RegexNode.z42` | MODIFY | 加 kind 9/10 factory + 注释更新 |
| `src/libraries/z42.regex/src/RegexParser.z42` | MODIFY | `\b` `\B` 转 WORD_BOUNDARY / WORD_NON_BOUNDARY |
| `src/libraries/z42.regex/src/Regex.z42` | MODIFY | inline flag 多字符解析；`_multiline` 字段；engine kind 9/10 + multi-line anchor |
| `src/libraries/z42.regex/tests/regex_word_boundary.z42` | NEW | `\b` / `\B` 单测 |
| `src/libraries/z42.regex/tests/regex_multiline.z42` | NEW | `(?m)` + `^` / `$` 多行单测 |
| `docs/design/stdlib/regex.md` | MODIFY | 决策表 + Deferred 段 + 落地引用 |

**只读引用**：

- `docs/design/stdlib/regex.md`（先读现状再改决策表）
- `src/libraries/z42.regex/tests/regex_inline_flags.z42`（参考 `(?i)` 单测形态）

## Out of Scope

- `(?s)` DOTALL flag（`.` 匹配换行）— 用例少，独立 follow-up
- 中间作用域的 inline flag `(?i:...)` / `(?-i:...)`
- `(?<=...)` / `(?=...)` lookahead / lookbehind
- 把引擎切到 Thompson NFA — Deferred / Future Work 长期项，不在本 spec
- 性能优化（`\b` 频繁触发 backtrack 时的代价）
- Unicode word 定义（当前 `\w` 仍是 ASCII；`\b` 同样按 ASCII word char）

## Open Questions

- [ ] 无 — 设计与 Decision 6/7 既有方向对齐，Python / .NET / Java 三方
      `(?m)` 语义一致，按它实现即可。
