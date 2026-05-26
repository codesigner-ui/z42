# Design: Triple-quoted raw string literal

## Architecture

```
源文件                Lexer                       Parser           AST
"""abc"""    ──▶  RawStringLiteral Token  ──▶  NudFn(Raw)  ──▶  LitStrExpr("abc")
                  Text="\"\"\"abc\"\"\""        (Text[3..^3])    Value="abc"
                                                NO UnescapeString
```

四个文件接力，互不影响 codegen / TypeCheck / VM。

## Decisions

### Decision 1: Token kind 分离（新 `RawStringLiteral`）vs 复用 `StringLiteral`

**问题：** 怎么让 parser 知道这是 raw（不要 unescape、剥 3 而非 1 个 quote）？

**选项：**

- **A. 新 `TokenKind.RawStringLiteral`**：parser NudTable 加一条独立入口；剥 3 字符 + skip unescape 内聚在一处
- **B. 复用 `StringLiteral`** + Token.Text 形态判断（首字符 `"` 还是 `"""`）：parser 入口少一个，但每次匹配 nud 时都要做 prefix 检查；和 `InterpolatedStringLiteral` 走独立 token 的风格不一致

**决定：** A — 与已有 `StringLiteral` / `CharLiteral` / `InterpolatedStringLiteral` 一致风格（每种 string 形态独立 token），parser 不引入运行时分支。

### Decision 2: Lexer 顺序（StringRules 中 `"""` 在 `"` 之前）

**问题：** `StringRules` 是 first-match，`"\""` 是 `"\"\"\""` 的前缀。如果顺序反了，`"""...`首先匹配空 `""` 然后立刻又匹配 `""` 然后被错误识别。

**决定：** 在 `LexRules.cs` 把 `("\"\"\"", TokenKind.RawStringLiteral, IsRaw: true)` 放在 `("\"", TokenKind.StringLiteral)` 之**前**。最长前缀优先原则。

**风险：** 现有代码 `s = "";` （空字符串）是否会被误识别为 raw 开始？分析：

- `""` 后面如果跟 `;`、`,`、`\n` 等普通 token → 不是 `"""`，retry 走普通 `"\""` 规则
- `""` 后面如果跟 `"` （`"""`）→ 这是 raw 开始
- 唯一冲突：`"";` 紧接着 `"abc"` 中间无空白 → `"" "abc"`（两个相邻字符串字面量）

Z42 不支持 C 风格相邻字符串字面量自动拼接（必须 `+`），所以 `"""abc"` 是 raw 字面 `abc"`（未闭合错误）。这是符合直觉的：先 emit raw open 后等闭合 `"""`。

考虑过分析：source 里出现 `"""` 序列的位置都是用户意图 raw string；任何 false-positive 都意味着已有源代码相邻空字符串字面量 + 另一字面量没空格，z42 文法本就不允许（要 `+`），所以**无 breaking change**。

### Decision 3: Body lex 扫描算法

**简单状态机**：

```
state = OPEN
loop:
    if pos+3 > len: lex error "unterminated raw string"
    if source[pos..pos+3] == "\"\"\"":
        Advance(3); break
    Advance(1)
```

无 escape 处理（不识别 `\`），无 newline 计数（Lexer.Advance 已经自带换行追踪 `_line` / `_col` for Span），无 interpolation。

**复杂度**：O(n) 扫描，n = string body 字节数。最坏 case 文件级 raw string ~1 KB / 量级稳定。

### Decision 4: Parser unescape skip

`ExprParser.cs` NudTable 加：

```csharp
[TokenKind.RawStringLiteral] = new((c, t, _, _) =>
    Ok(new LitStrExpr(t.Text[3..^3], t.Span), c)),
```

注意 **没有 `UnescapeString` 调用**。`t.Text[3..^3]` 直接做字面值。

### Decision 5: 文档落点

| 文档 | 角色 |
|------|------|
| `docs/design/language/language-overview.md` | 把当前的 `"""..."""` 示例段从"草稿"升级为"L1 正式规范"，加 v0 限制清单 |
| `docs/design/language/raw-string-literal.md` (NEW) | 完整设计 doc + Deferred 段 — 跟 features.md 同等深度 |
| `docs/design/language/grammar.peg` | 加 raw string production，删占位注释 |
| `examples/raw_string_basic.z42` (NEW) | 单文件示范 6 种典型用法 |

## Implementation Notes

### Lexer.cs LexStringBody 改动

`LexRules.StringRule` 加 `bool IsRaw = false`。`LexStringBody` 增加 raw 分支：

```csharp
else if (rule.IsRaw)
{
    // Raw string """..."""
    while (_pos + 2 < source.Length)
    {
        if (source[_pos] == '"' && source[_pos + 1] == '"' && source[_pos + 2] == '"')
        {
            Advance(); Advance(); Advance();  // closing """
            return new Token(...);
        }
        Advance();
    }
    // unterminated
    throw new LexException("unterminated raw string literal", MakeSpan(startPos, startLine, startCol));
}
```

注意现有 `else` 分支（普通字符串）的 escape 处理保留不动。

### TokenKind enum 加位置

末尾追加 `RawStringLiteral`（不要插中间，避免 ABI / token-id 漂移 — z42 .NET 编译器不持久化 token id，所以技术上无影响，但保持 append 习惯）。

### LexException 类型

观察现有 lexer，错误是用 `throw new ParseException(...)` 还是有专门 `LexException`？让 design doc 直接抄现状：先 grep，然后写明用什么类型。

**实施期注**：核对一下 `Lexer.cs` 现有错误抛法（`throw new ...`），跟随同一个 exception 类型。

## Testing Strategy

### LexerTests.cs（C# 单元）

- `test_raw_string_single_line` — `"""hi"""` → 1 token, Text == `"""hi"""`
- `test_raw_string_multi_line` — 3 行 raw，Token.Text 含 `\n` 字节
- `test_raw_string_empty` — `""""""` → empty
- `test_raw_string_embeds_single_quote` — `"""he said "hi"."""`
- `test_raw_string_embeds_double_quote` — `"""x ""y"""`
- `test_raw_string_keeps_backslash_literally` — `"""\n"""` → Token.Text 含字面 `\` + `n`
- `test_raw_string_unterminated_at_eof_throws` — `"""abc` 无闭合 → throw
- `test_raw_string_not_split_by_double_quote` — `""` 在中间不算闭合

### Parser golden（GoldenTests.cs 添加用例）

- `raw_str_single_line.z42` + `raw_str_single_line.bound.txt` — `LitStrExpr` 值剥分隔符
- `raw_str_multi_line.z42` + bound — multi-line 字面值

### VM golden test（end-to-end）

`src/tests/raw_string/basic/source.z42` + `expected_output.txt`：

```z42
namespace Z42RawStringBasic;
void Main() {
    string a = """hello""";
    string b = """multi
line""";
    string c = """\n stays literal""";
    string empty = """""";
    Console.WriteLine(a);
    Console.WriteLine(b);
    Console.WriteLine(c);
    Console.WriteLine("[" + empty + "]");
}
```

期望输出：

```
hello
multi
line
\n stays literal
[]
```

### scripts/test-all.sh

确认 dotnet test + test-vm + test-stdlib 全绿。
