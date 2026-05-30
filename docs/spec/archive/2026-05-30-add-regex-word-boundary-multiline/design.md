# Design: Regex `\b` / `\B` + `(?m)` multiline

## Architecture

```
pattern source string
     │
     ▼
Regex.Compile
  ├─ ⓘ inline flag stripper（升级）
  │     · 旧：if pattern startsWith "(?i)" → strip 4 chars, set _caseInsensitive
  │     · 新：扫前缀 "(?…)"，对 _flags ∈ {i, m} 逐字符 set，剥离整段
  │
  └─ RegexParser.ParsePattern(剩余 body)
        └─ ParseEscape  ── 新增分支：
              `\b` → RegexNode(kind=9)   (WORD_BOUNDARY)
              `\B` → RegexNode(kind=10)  (WORD_NON_BOUNDARY)

new Regex(... , _caseInsensitive, _multiline)
     │
     ▼
Regex.Match(seq, count, idx, pos)  ── 新增分支：
   k == 9  : if _atWordBoundary(pos)        → recurse idx+1, pos
   k == 10 : if !_atWordBoundary(pos)       → recurse idx+1, pos
   k == 4  : if pos==0 || (_multiline && input[pos-1]=='\n') → recurse
   k == 5  : if pos==len || (_multiline && input[pos]=='\n') → recurse
```

## Decisions

### Decision 1: 用新 RegexNode kind 而非 ANCHOR_START/END 的子类

**问题**：`\b` / `\B` 是零宽匹配，能不能复用 ANCHOR_START / END？

**选项：**
- A — 新增 kind 9/10
- B — 复用 kind 4/5 加一个子类标记字段

**决定**：A。

**理由**：现有 RegexNode 是 discriminated union，kind 决定字段使用。复用 kind 4/5 要给 ANCHOR_START 多一个标志字段（`_kind4Subkind`），破坏单层 dispatch 的清晰度。新增 kind 0 cost，且与"ANCHOR_START / END" 命名语义解耦：`\b` 不是 anchor，是 word transition predicate。

### Decision 2: `_multiline` 影响现有 kind 4/5 vs 新增 kind 4M/5M

**问题**：multiline 模式下 `^`/`$` 语义变了。怎样表示？

**选项：**
- A — 复用 kind 4/5；engine `Match` 根据 `_multiline` field 切换语义
- B — Compile 阶段重写：multiline 下把 `^` 转成 kind 4M（新 kind）

**决定**：A。

**理由**：
- multiline 是 Regex 实例级 flag，不会跨 Compile 改变；engine 只需一次 if 切换
- B 方案需要 parser 知道 multiline flag（耦合）、或 Compile 后扫一遍 AST 替换（额外 pass）
- `_multiline` 本就是 Regex field（同 `_caseInsensitive`），engine 直接读

### Decision 3: `_atWordBoundary` 实现

```z42
private bool _atWordBoundary(int pos) {
    bool leftWord  = pos > 0
                  && _isWordChar((int)this._input.CharAt(pos - 1));
    bool rightWord = pos < this._input.Length
                  && _isWordChar((int)this._input.CharAt(pos));
    return leftWord != rightWord;
}

private static bool _isWordChar(int ch) {
    return (ch >= 48 && ch <= 57)    // 0-9
        || (ch >= 65 && ch <= 90)    // A-Z
        || (ch >= 97 && ch <= 122)   // a-z
        || ch == 95;                 // _
}
```

**问题**：case-insensitive 模式下 `\w` 已经覆盖两 case，`_isWordChar` 要不要折叠？

**决定**：不需要。ASCII 字母无论 case，二者都在 `[A-Za-z0-9_]` 内，`_isWordChar` 返回值不受 case 影响。`(?i)` 与 `\b` 互不污染。

### Decision 4: inline flag 解析放在 Regex.Compile 还是 RegexParser

**问题**：当前 `(?i)` 解析是 Regex.Compile 顶层字符匹配，绕过 RegexParser。新方案要解析 `(?im)`/`(?mi)`/`(?m)`，仍放外层吗？

**选项：**
- A — 仍在 Regex.Compile 做字符级 strip
- B — 升级 RegexParser.ParseAtom 的 `(?…)` dispatch，加 `(?im)` 这种 group-less inline flag 节点（或 `(?…:` 局部作用域形态）

**决定**：A。

**理由**：
- B 才能支持 `(?im:expr)` / 中间作用域，但本 spec 显式 out-of-scope
- A 路径已经有现成代码，扩展只需把"匹配 `(?i)`"换成"匹配 `(?` + [im]+ + `)`"
- B 路径要重排 RegexNode 的 GROUP / 非捕获语义，影响面大

### Decision 5: 重复 flag char（`(?ii)` / `(?im m)`）容忍度

**决定**：单字符 `[im]` 重复（如 `(?ii)`）容忍：等价于 `(?i)`。多空格 / 其他字符不接受。

**理由**：与 .NET `RegexOptions` 语义一致；Python `re` 也不重复报错。避免 corner case 抛 RegexException 增加调用方负担。

### Decision 6: charset 内 `\b` 仍为字面 `b`

**问题**：PCRE 在 charset 内 `[\b]` 是 ASCII BS (0x08)；本 v0 是字面 `b`。要不要顺手统一？

**决定**：保留字面 `b` 行为。

**理由**：
- ASCII BS 是退格控制字符，z42 用例极罕见
- 改 charset 行为会 break 任何 `[\b]` 的现有 (虽然不太可能) 用例，与"不为旧版本提供兼容"无关，是无需求改动 → 删
- 真要 ASCII BS，调用方写 `\x08` 等价；不过 z42.regex v0 也未实现 `\x` — 同延后

## Implementation Notes

### RegexNode 字段最小集

- kind 9/10 不需要任何 payload；构造器直接 `new RegexNode(9)` / `new RegexNode(10)`
- 加两个静态 factory：`WordBoundary()` / `WordNonBoundary()`

### Regex 字段新增

```z42
private bool _multiline;     // (?m) flag
```

构造器多一个参数；现有 `(?i)`-only 构造路径默认传 `false`。

### inline-flag 解析

```z42
public static Regex Compile(string pattern) {
    bool caseInsensitive = false;
    bool multiline       = false;
    string body          = pattern;
    // 试匹配 "(?…)" 前缀，… 是 [im]+
    if (pattern.Length >= 4
            && pattern.CharAt(0) == '('
            && pattern.CharAt(1) == '?') {
        // 扫描 [im]+；遇到 ')' 提交，遇到其他字符 abort（保留给 parser）
        int j = 2;
        bool ci = false;
        bool ml = false;
        bool ok = true;
        while (j < pattern.Length && pattern.CharAt(j) != ')') {
            char fc = pattern.CharAt(j);
            if (fc == 'i')      { ci = true; }
            else if (fc == 'm') { ml = true; }
            else { ok = false; break; }
            j = j + 1;
        }
        if (ok && j < pattern.Length && pattern.CharAt(j) == ')' && j > 2) {
            caseInsensitive = ci;
            multiline       = ml;
            body            = pattern.Substring(j + 1, pattern.Length - j - 1);
        }
    }
    ...
}
```

- `j > 2` 保证至少剥离了一个 flag char（即 `(?)` 不匹配）
- 任何不在 `{i, m}` 的字符触发 `ok = false`，保持原 body，让 parser 报错

### engine 改动

```z42
// k == 4 ANCHOR_START
if (pos == 0) { return this.Match(seq, count, idx + 1, pos); }
if (this._multiline && pos > 0
    && (int)this._input.CharAt(pos - 1) == 10) {
    return this.Match(seq, count, idx + 1, pos);
}
return -1;

// k == 5 ANCHOR_END
if (pos == this._input.Length) { return this.Match(seq, count, idx + 1, pos); }
if (this._multiline && pos < this._input.Length
    && (int)this._input.CharAt(pos) == 10) {
    return this.Match(seq, count, idx + 1, pos);
}
return -1;

// k == 9 WORD_BOUNDARY
if (this._atWordBoundary(pos)) { return this.Match(seq, count, idx + 1, pos); }
return -1;

// k == 10 WORD_NON_BOUNDARY
if (!this._atWordBoundary(pos)) { return this.Match(seq, count, idx + 1, pos); }
return -1;
```

### Parser 改动

`ParseEscape` 加两行：

```z42
if (e == 'b') { return RegexNode.WordBoundary(); }
if (e == 'B') { return RegexNode.WordNonBoundary(); }
```

放在 `\d\D\w\W\s\S` 之后、字面 fallback 之前。

## Testing Strategy

### regex_word_boundary.z42（~12 tests）

- `\bcat\b` 单词命中 / `concatenate` 失配
- `\bcat\b` 首末位 word char
- `\b\w+\b` FindAll 多词
- `\B` 词中央命中 / 独立词失配
- `\B` 与 `\b` 互斥（同位置必恰一）
- 与 `(?i)` 组合：`(?i)\bCat\b` 命中 "CAT"
- 与 quantifier 组合：`\b\d+\b` 命中独立数字

### regex_multiline.z42（~10 tests）

- 单行模式（无 `(?m)`）下 `^foo` 在 `bar\nfoo` 中失配（回归）
- `(?m)^foo` 在 `bar\nfoo` 中命中
- `(?m)foo$` 在 `foo\nbar` 中命中
- `(?m)^\w+$` FindAll 三行各命中一次
- `(?m)` 输入末位无尾随 `\n` 仍 `$` 命中
- 空字符串 `(?m)^$` 命中 pos=0
- `(?im)^FOO$` 在 `BAR\nfoo` 中命中
- 单行 `\n` 作为字面字符匹配（`\n` 字面不受 multiline 影响 — 仍是普通 LIT）

### 现有回归

- 跑全 regex 测试集（11 个文件），确保 `(?i)` / inline-flags / 现有
  `^$` 行为不漂移

### GREEN 验证

- `dotnet build && cargo build`
- `./scripts/build-stdlib.sh`（z42.regex zpkg 重建）
- `./scripts/test-stdlib.sh z42.regex`（z42 [Test] 运行）
- `./scripts/test-all.sh`（含 cross-zpkg，确保 dep-index 不受影响）
