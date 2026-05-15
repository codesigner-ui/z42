# Tasks: add z42.regex

> 状态：🟢 已完成 | 创建：2026-05-16 | 完成：2026-05-16 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

stdlib roadmap P1/P2 表里的 `z42.regex`：正则表达式 parser + 匹配引擎。
对标 C# `System.Text.RegularExpressions.Regex` + Python `re` + JavaScript `RegExp`
（语义子集；不抄 RE2 / Rust 的 NFA simulation，v0 直接用 backtracking 引擎）。

z42.text 当前有 `Regex` 占位但未实现；本 spec 落地。

## API Surface (v0)

```z42
namespace Std.Regex;

public class Regex {
    public static Regex Compile(string pattern);

    public bool IsMatch(string input);
    public Match Find(string input);           // 第一个 match 或 null
    public List<Match> FindAll(string input);
    public string Replace(string input, string replacement);
    public List<string> Split(string input);
}

public class Match {
    public int    Start();
    public int    End();
    public int    Length();
    public string Value();
    public string Group(int index);            // 0 = entire match
    public int    GroupCount();
}

public class RegexException : Exception { }
```

## 支持的语法（v0）

| 语法 | 含义 |
|------|------|
| 字面字符 | `a`, `1`, `_` 等 |
| `.` | 任意单字符（除换行可选） |
| `^` `$` | 字符串首 / 末锚点 |
| `\\` `\.` `\*` `\(` `\[` 等 | 转义 metachar |
| `\n` `\t` `\r` | 控制字符 |
| `\d` `\D` `\w` `\W` `\s` `\S` | shortcut 字符类 |
| `[abc]` `[a-z]` `[^abc]` | 字符类（正/负、范围） |
| `?` `*` `+` | quantifier（greedy only） |
| `{n}` `{n,m}` | 计数 quantifier |
| `()` | capturing group |
| `\|` | alternation |

## 不支持（v0 Deferred — 详 design doc）

- backreference `\1` `\2`
- non-greedy `*?` `+?` `??`
- lookahead/lookbehind `(?=)` `(?!)` `(?<=)` `(?<!)`
- named group `(?<name>...)`
- non-capturing group `(?:...)`
- Unicode property classes `\p{L}` / `\P{L}`
- atomic group / possessive quantifier
- flags（i / m / s）

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 引擎 | (a) backtracking NFA / (b) Thompson NFA sim (RE2-style) / (c) NFA→DFA | (a) | 简单实现（200 LOC），同 Python/Java/JavaScript；ReDoS 风险 documented |
| 2. AST 表达 | (a) discriminated union 类 / (b) sealed inheritance | (a) | z42 无 ADT；用 `_kind: int` + 各 field（同 TomlValue/JsonValue 模式） |
| 3. Compile-time vs run-time check | (a) compile 时 validate / (b) match 时 lazy | (a) | fail-fast；`Compile()` 直接抛 RegexException |
| 4. 字符类范围 | (a) 区间逐对存储 / (b) bitset 256 位 | (a) | 范围少时省内存；charset 检查 O(N) but N typically < 10 |
| 5. Greedy 实现 | (a) max attempts then backoff / (b) lazy attempts | (a) | greedy 默认；non-greedy 留 Deferred |
| 6. 大小写敏感 | sensitive only / flag | sensitive only | i flag 留 Deferred |
| 7. Multiline `^$` | (a) 始终匹配字符串首末 / (b) 行首末（m flag）| (a) | v0 无 flag；多行匹配留 Deferred |
| 8. 异常类 | RegexException 在 `Std` namespace | yes | 同 UriException / TomlException / JsonException 模式 |
| 9. Namespace 层级 | `Std.Text.Regex`（z42.text 占位下）/ `Std.Regex`（top-level）| `Std.Regex` | 避免 z42.io.binary 一样的三段 namespace bug；`Std.Text.Regex` 占位类 deprecated（v0 简单替换） |
| 10. Group 0 语义 | 0 = entire match / 1 = first group | 0 = entire | 同 C# / Python / Java |

## 阶段 1: 包骨架

- [x] 1.1 NEW `src/libraries/z42.regex/z42.regex.z42.toml` — manifest（dep on z42.core + z42.collections for List）
- [x] 1.2 NEW `src/libraries/z42.regex/src/RegexException.z42` — `Std.RegexException`
- [x] 1.3 NEW `src/libraries/z42.regex/src/RegexNode.z42` — AST 节点（_kind + fields）
- [x] 1.4 NEW `src/libraries/z42.regex/src/RegexParser.z42` — pattern → AST
- [x] 1.5 NEW `src/libraries/z42.regex/src/Match.z42` — Match 类（start/end/groups）
- [x] 1.6 NEW `src/libraries/z42.regex/src/Regex.z42` — main class + backtracking engine

## 阶段 2: 测试

- [x] 2.1 NEW `tests/regex_literal.z42` — 字面字符 + anchors + escape
- [x] 2.2 NEW `tests/regex_quantifiers.z42` — `?` `*` `+` `{n,m}`
- [x] 2.3 NEW `tests/regex_charclass.z42` — `[abc]` `[a-z]` `[^...]` `\d \w \s`
- [x] 2.4 NEW `tests/regex_groups.z42` — `()` group + alternation `|`
- [x] 2.5 NEW `tests/regex_replace.z42` — Replace + Split
- [x] 2.6 NEW `tests/regex_errors.z42` — 非法 pattern 抛 RegexException

## 阶段 3: Wiring + docs

- [x] 3.1 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.regex"`
- [x] 3.2 MODIFY `scripts/build-stdlib.sh` 加 LIBS + index.json `Std.Regex`
- [x] 3.3 NEW `src/libraries/z42.regex/README.md`
- [x] 3.4 NEW `docs/design/stdlib/regex.md`
- [x] 3.5 MODIFY `docs/design/stdlib/roadmap.md` + `organization.md` + `src/libraries/README.md`

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/build-stdlib.sh` 全绿
- [x] 4.2 `./scripts/test-stdlib.sh z42.regex` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.4 mv → `docs/spec/archive/2026-05-16-add-z42-regex/`
- [x] 4.5 commit + push

## 实施期发现

1. **`out` 是 z42 保留字**（probably `out` param modifier 占用）。Replace 内
   `string out` 变量编译报 `unexpected token out`。改名为 `acc`；parser 内
   类似冲突的 `RegexNode[] out` 改为 `wrap`。
2. **z42 stdlib 不用 `List<T>`**（generic 类型参数 dropping bug，TomlValue 注释
   已记录）。Regex AST 用 raw `RegexNode[]` + 显式 count 字段。
3. **`_peek()` 越界必须显式 `_eof()` 守卫**。Pattern `a{3` 末尾 parser 调用
   `this._peek() != '}'` → `CharAt(pos == length)` 抛 IndexOutOfRange 而非
   `RegexException`，测试 `test_unclosed_quant_brace_throws` 因此 fail（捕获
   IndexOutOfRange ≠ RegexException）。所有 `_peek()` 比较前加 `!this._eof() &&`。
