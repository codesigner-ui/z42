# Spec: stdlib-string

## ADDED Requirements

### Requirement: 最小字符串 intrinsic 核心

String 类型对外暴露 7 个 extern 作为"最小核"，供脚本方法调用：`Length` / `ToString` / `Equals` / `CompareTo` / `GetHashCode` / `Format` / `Split` 保留现状；新增 `CharAt(int)` 和静态 `FromChars(char[])`。

#### Scenario: Length 返回 Unicode scalar 数
- **WHEN** 调用 `"hello".Length`
- **THEN** 返回 5
- **WHEN** 调用 `"".Length`
- **THEN** 返回 0

#### Scenario: CharAt 按字符索引取值
- **WHEN** 调用 `"hello".CharAt(1)`
- **THEN** 返回字符 `'e'`
- **WHEN** 调用 `"hello".CharAt(0)` / `"hello".CharAt(4)`
- **THEN** 分别返回 `'h'` / `'o'`

#### Scenario: CharAt 越界为运行时错误
- **WHEN** 调用 `"abc".CharAt(5)`
- **THEN** VM trap / 运行时异常（与 ArrayGet 越界行为一致）

#### Scenario: FromChars 构造字符串
- **WHEN** 调用 `string.FromChars(new char[] { 'h', 'i' })`
- **THEN** 返回 `"hi"`
- **WHEN** 传入空数组 `new char[0]`
- **THEN** 返回 `""`

### Requirement: Char intrinsic 扩展

Char 类型新增 3 个 extern 方法以支持脚本化字符串处理。

#### Scenario: IsWhiteSpace 识别空白字符
- **WHEN** 调用 `' '.IsWhiteSpace()` / `'\t'.IsWhiteSpace()` / `'\n'.IsWhiteSpace()` / `'\r'.IsWhiteSpace()`
- **THEN** 全返回 true
- **WHEN** 调用 `'a'.IsWhiteSpace()` / `'0'.IsWhiteSpace()`
- **THEN** 返回 false

#### Scenario: ToLower / ToUpper ASCII 规则
- **WHEN** 调用 `'A'.ToLower()` / `'Z'.ToLower()`
- **THEN** 分别返回 `'a'` / `'z'`
- **WHEN** 调用 `'a'.ToUpper()` / `'z'.ToUpper()`
- **THEN** 分别返回 `'A'` / `'Z'`
- **WHEN** 调用 `'1'.ToLower()` / `'!'.ToUpper()`
- **THEN** 分别返回 `'1'` / `'!'`（非字母返回自身）

### Requirement: String 方法脚本实现

以下方法以脚本 body 实现，行为与原 extern 语义等价。

#### Scenario: IsEmpty
- **WHEN** `"".IsEmpty`
- **THEN** true
- **WHEN** `"x".IsEmpty`
- **THEN** false

#### Scenario: Contains
- **WHEN** `"hello world".Contains("world")`
- **THEN** true
- **WHEN** `"hello".Contains("xyz")`
- **THEN** false
- **WHEN** `"hello".Contains("")`
- **THEN** true（C# 行为）

#### Scenario: StartsWith
- **WHEN** `"hello".StartsWith("he")`
- **THEN** true
- **WHEN** `"hello".StartsWith("hello!")`
- **THEN** false
- **WHEN** `"hello".StartsWith("")`
- **THEN** true

#### Scenario: EndsWith
- **WHEN** `"hello".EndsWith("lo")`
- **THEN** true
- **WHEN** `"hello".EndsWith("xlo")`
- **THEN** false
- **WHEN** `"hello".EndsWith("")`
- **THEN** true

#### Scenario: IndexOf
- **WHEN** `"hello world".IndexOf("world")`
- **THEN** 返回 6
- **WHEN** `"hello".IndexOf("xyz")`
- **THEN** 返回 -1
- **WHEN** `"aaab".IndexOf("ab")`
- **THEN** 返回 2

#### Scenario: Substring（单参 / 双参）
- **WHEN** `"hello world".Substring(6)`
- **THEN** 返回 `"world"`
- **WHEN** `"hello world".Substring(0, 5)`
- **THEN** 返回 `"hello"`
- **WHEN** `"abc".Substring(3)` / `"abc".Substring(0, 0)`
- **THEN** 返回 `""`

#### Scenario: Replace
- **WHEN** `"a.b.c".Replace(".", "-")`
- **THEN** 返回 `"a-b-c"`
- **WHEN** `"foo".Replace("x", "y")`
- **THEN** 返回 `"foo"`
- **WHEN** `"aaa".Replace("a", "bb")`
- **THEN** 返回 `"bbbbbb"`

#### Scenario: ToLower / ToUpper（ASCII 规则）
- **WHEN** `"Hello World".ToLower()`
- **THEN** 返回 `"hello world"`
- **WHEN** `"hello world".ToUpper()`
- **THEN** 返回 `"HELLO WORLD"`
- **WHEN** `"Hello 123!".ToLower()`
- **THEN** 返回 `"hello 123!"`

#### Scenario: Trim / TrimStart / TrimEnd
- **WHEN** `"  hello  ".Trim()`
- **THEN** 返回 `"hello"`
- **WHEN** `"  hello".TrimStart()`
- **THEN** 返回 `"hello"`
- **WHEN** `"hello  ".TrimEnd()`
- **THEN** 返回 `"hello"`
- **WHEN** `"   ".Trim()`
- **THEN** 返回 `""`

#### Scenario: IsNullOrEmpty / IsNullOrWhiteSpace
- **WHEN** `string.IsNullOrEmpty(null)` / `string.IsNullOrEmpty("")`
- **THEN** 全为 true
- **WHEN** `string.IsNullOrEmpty("x")`
- **THEN** false
- **WHEN** `string.IsNullOrWhiteSpace("   \t\n")` / `string.IsNullOrWhiteSpace("")` / `string.IsNullOrWhiteSpace(null)`
- **THEN** 全为 true
- **WHEN** `string.IsNullOrWhiteSpace("  x  ")`
- **THEN** false

### Requirement: extern 预算净减少

String.z42 的 `[Native]` extern 方法数量从 22 降至 ≤ 9；VM 侧 `__str_*` builtin 数量从 22 降至 ≤ 10（算上保留的 Split / Format / Equals / CompareTo / Hash / ToString / Length / CharAt / FromChars）。

## REMOVED Requirements

以下 VM builtin 函数被删除（已被脚本实现取代）：

- `__str_contains`
- `__str_starts_with`
- `__str_ends_with`
- `__str_index_of`
- `__str_replace`
- `__str_to_lower`
- `__str_to_upper`
- `__str_trim`
- `__str_trim_start`
- `__str_trim_end`
- `__str_substring`（两个重载）
- `__str_is_null_or_empty`
- `__str_is_null_or_whitespace`

## MODIFIED Requirements

### Substring / IndexOf 索引语义

**Before:** `Length` 按 Unicode scalar 计数，但 `Substring(start, length)` / `IndexOf(sub)` 按 UTF-8 byte 偏移。

**After:** 所有索引 / 长度 / 切片一律按 Unicode scalar (char) 计数。ASCII 场景行为不变；非 ASCII 场景下新语义与 C# / Java 一致。

## IR Mapping

无新 IR 指令。所有新增 extern (`__str_char_at` / `__str_from_chars` / `__char_is_whitespace` / `__char_to_lower` / `__char_to_upper`) 通过现有 `BuiltinInstr` 派发。脚本方法由常规 `CallInstr` 调用（与 op_Add 的 AddInstr 机制不同，方法名不会被 inline）。

## Pipeline Steps

- [ ] Lexer：无改动
- [ ] Parser / AST：无改动
- [ ] TypeChecker：无改动（extern 签名解析已支持；脚本方法体解析已支持）
- [ ] IR Codegen：无改动（BuiltinInstr 发射路径已存在）
- [ ] VM interp：新增 5 个 builtin + 注册，删除 13 条 builtin + 反注册
