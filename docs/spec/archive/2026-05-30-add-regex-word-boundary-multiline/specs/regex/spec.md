# Spec: Regex `\b` / `\B` + `(?m)` multiline

## ADDED Requirements

### Requirement: `\b` 词边界（零宽）

`\b` 在"两侧字符的 word-ness 不同"时匹配（零宽，不消耗字符）。
"word char" 定义同 `\w`：`[A-Za-z0-9_]`。
输入两端虚构为 non-word，因此 `\b` 在首末（与 word char 邻接时）匹配。

#### Scenario: 词头与词尾匹配

- **WHEN** pattern `\bcat\b`，input `the cat sat`
- **THEN** Find 返回 start=4, end=7, Value="cat"

#### Scenario: 词中央不匹配

- **WHEN** pattern `\bcat\b`，input `concatenate`
- **THEN** Find 返回 null

#### Scenario: 与 escape / charset 组合

- **WHEN** pattern `\b\w+\b`，input `foo bar baz`
- **THEN** FindAll 长度为 3，依次返回 "foo" / "bar" / "baz"

#### Scenario: 输入首字符是 word char

- **WHEN** pattern `\bx`，input `xyz`
- **THEN** Find 返回 start=0, end=1, Value="x"

#### Scenario: 输入末位是 word char

- **WHEN** pattern `z\b`，input `xyz`
- **THEN** Find 返回 start=2, end=3, Value="z"

### Requirement: `\B` 非词边界（零宽）

`\B` 在"两侧字符的 word-ness 相同"时匹配（即不在 `\b` 位置）。

#### Scenario: 词中央匹配

- **WHEN** pattern `\Bcat\B`，input `concatenate`
- **THEN** Find 返回包含 "cat" 的位置（start=3）

#### Scenario: 独立词不匹配

- **WHEN** pattern `\Bcat\B`，input `the cat sat`
- **THEN** Find 返回 null

### Requirement: `(?m)` multiline 模式 — `^` 匹配换行后

启用 `(?m)` 后，`^` 在 pos=0 或前一字符为 `\n` (0x0A) 时匹配。

#### Scenario: 单行模式下，行首 `^` 不匹配

- **WHEN** pattern `^foo`（无 `(?m)`），input `bar\nfoo`
- **THEN** Find 返回 null

#### Scenario: 多行模式下，行首 `^` 匹配换行后

- **WHEN** pattern `(?m)^foo`，input `bar\nfoo`
- **THEN** Find 返回 start=4, end=7, Value="foo"

#### Scenario: pos=0 始终匹配

- **WHEN** pattern `(?m)^x`，input `x\ny`
- **THEN** Find 返回 start=0, end=1

### Requirement: `(?m)` multiline 模式 — `$` 匹配换行前

启用 `(?m)` 后，`$` 在 pos == input.Length 或当前字符为 `\n` 时匹配。

#### Scenario: 单行模式下，`$` 仅末尾

- **WHEN** pattern `foo$`，input `foo\nbar`
- **THEN** Find 返回 null

#### Scenario: 多行模式下，`$` 也匹配 `\n` 前

- **WHEN** pattern `(?m)foo$`，input `foo\nbar`
- **THEN** Find 返回 start=0, end=3, Value="foo"

#### Scenario: 末尾仍匹配

- **WHEN** pattern `(?m)bar$`，input `foo\nbar`
- **THEN** Find 返回 start=4, end=7

### Requirement: 多行模式 + FindAll 跨行

#### Scenario: 多行 `^...$` 每行命中

- **WHEN** pattern `(?m)^\w+$`，input `foo\nbar\nbaz`
- **THEN** FindAll 长度为 3，依次返回 "foo" / "bar" / "baz"

### Requirement: 组合 inline flags `(?im)` / `(?mi)`

`Regex.Compile` 接受 `(?im)` / `(?mi)` 同时启用 case-insensitive +
multiline；顺序不敏感。

#### Scenario: `(?im)` 同时启用两 flag

- **WHEN** pattern `(?im)^foo$`，input `BAR\nFOO`
- **THEN** Find 返回 start=4, end=7, Value="FOO"

#### Scenario: 不识别的 inline flag 不触发剥离

- **WHEN** pattern `(?x)abc`（`x` 不在已实现集合中）
- **THEN** Compile 抛 RegexException（`(?...)` 在 ParseAtom 既有
  unsupported `(?…)` 路径报错）

### Requirement: `\b` / `\B` 转义在 charset 内部仍为字面

字符类 `[\b]` 中的 `\b` 当前 v0 仍为字面 `b`（与现行 ParseCharsetChar
行为一致；ASCII BS 0x08 不在本 spec 范围）。

#### Scenario: charset 内 `\b` 字面化

- **WHEN** pattern `[\b]`，input `b`
- **THEN** IsMatch 返回 true（match 字面 `b`）

## MODIFIED Requirements

### Requirement: ANCHOR_START / ANCHOR_END 语义随 `_multiline` 切换

**Before:**
- `^` (k=4)：`pos == 0` 时匹配
- `$` (k=5)：`pos == input.Length` 时匹配

**After:**
- `^` (k=4)：`pos == 0`；或 `_multiline && pos > 0 && input[pos-1] == '\n'`
- `$` (k=5)：`pos == input.Length`；或 `_multiline && pos < length && input[pos] == '\n'`

非多行模式下行为不变。

### Requirement: Regex.Compile inline flag 前缀解析升级

**Before:**

仅识别精确前缀 `(?i)`（4 字符），剥离后剩余交给 RegexParser。

**After:**

识别 `(?` + 任意 1+ 字符 ∈ `{i, m}` + `)` 的前缀；
- 设置对应 `_caseInsensitive` / `_multiline` 字段
- 字符顺序无关，但每字符至多出现一次（重复无意义但宽容接受，与 .NET 一致）
- 不匹配该前缀形态（含未知 flag char）→ 不剥离，原样交给 RegexParser；
  parser 既有 `(?...)` 路径会拒绝未知 `(?x)` 等构造

## IR Mapping

无新 IR — 纯 stdlib 改动。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [ ] TypeChecker — N/A
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A
