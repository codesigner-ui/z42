# Spec: Raw String Literal (`"""..."""`)

## ADDED Requirements

### Requirement: Lexer recognizes triple-quoted raw string

#### Scenario: 单行 raw string
- **WHEN** 源文件含 `string s = """hello""";`
- **THEN** lexer emit 单个 token `RawStringLiteral`，Text = `"""hello"""`（含分隔符）

#### Scenario: 多行 raw string
- **WHEN** 源文件含：
  ```z42
  string s = """line1
  line2
  line3""";
  ```
- **THEN** lexer emit 单个 token，Text 含两个换行符；token Span 覆盖全 4 行

#### Scenario: 空 raw string
- **WHEN** 源文件含 `string s = """""";`（6 个连续双引号）
- **THEN** lexer emit 单个 token，token 值 = `""` (empty string)

#### Scenario: raw string 内嵌单个 `"` 或 `""`
- **WHEN** 源文件含 `string s = """he said "hi" "" done""";`
- **THEN** lexer 把整个 `"""he said "hi" "" done"""` 当作 one token；单/双 quote 字符保留

#### Scenario: raw string 内嵌反斜杠不 escape
- **WHEN** 源文件含 `string s = """a\nb\tc""";`
- **THEN** 字面值 = `a\nb\tc`（6 个 ASCII 字符 + 2 个 `\`），**不**变成 `a<LF>b<TAB>c`

#### Scenario: raw string 内嵌 UTF-8 字符
- **WHEN** 源文件含 `string s = """名前: 太郎""";`
- **THEN** 字面值保留 UTF-8 字节序列，长度按 UTF-16 code unit 计

### Requirement: Lexer rejects unclosed raw string

#### Scenario: 文件末尾未闭合
- **WHEN** 源文件含 `string s = """unclosed`（文件 EOF 前无 `"""`）
- **THEN** 报 lexer 错误 `unterminated raw string literal`

#### Scenario: 单/双引号嵌入不构成闭合
- **WHEN** 源文件含 `"""he said "hi`（永远没有 3 连 quote）
- **THEN** 报同上 `unterminated raw string literal`

### Requirement: Parser produces LitStrExpr from RawStringLiteral

#### Scenario: AST LitStrExpr 值剥分隔符
- **WHEN** 表达式 `"""hello"""`
- **THEN** parser emit `LitStrExpr { Value = "hello" }`（剥 3+3 个 `"`）

#### Scenario: 字面 `\n` 字符不解码
- **WHEN** 表达式 `"""\n"""`
- **THEN** parser emit `LitStrExpr { Value = "\\n" }`（2 字符：`\` 和 `n`），**不**等同 `"\n"` 的 `<LF>`

### Requirement: Raw string in 表达式位置可用

#### Scenario: 赋值
- **WHEN** `string s = """abc""";`
- **THEN** `s` 字符串值 = `abc`

#### Scenario: 函数实参
- **WHEN** `Console.WriteLine("""line1\nline2""");`
- **THEN** stdout 输出 `line1\nline2`（含字面 `\n`），不换行

#### Scenario: 嵌入表达式中
- **WHEN** `string s = """a""" + "b";`
- **THEN** `s = "ab"`

### Requirement: Source text round-trip

#### Scenario: Token.Text 保留原始字节
- **WHEN** lexer 处理 `"""hi\nthere"""`
- **THEN** `Token.Text == "\"\"\"hi\\nthere\"\"\""`（即 Text 是 11 字符的源字符串）

## IR Mapping

无新增 IR 指令。`LitStrExpr` 编译到 `LoadStrConst`（已存在），常量字符串值进 const pool 与普通 string literal 共享路径。

## Pipeline Steps

受影响的 pipeline 阶段：
- [x] **Lexer** — 新 token kind + 新 string body 分支
- [x] **Parser / AST** — NudTable 新条目，复用既有 `LitStrExpr`
- [ ] **TypeChecker** — 无变化（`LitStrExpr` 已是 `string` 类型）
- [ ] **IR Codegen** — 无变化（走 `LoadStrConst`）
- [ ] **VM interp** — 无变化（const pool 加载已有）

## 非目标 / 不在本 spec 范围（v0 留 backlog）

- Variable-count quote delimiters（`""""..."""""` 等）
- Indent dedent（按 closing `"""` 缩进剥共同前缀）
- Interpolation prefix `$"""..."""`
- Leading/trailing newline auto-trim
- `@"..."` 单引号 verbatim 形式
- `'''...'''` 三单引号形式
