# Design: 将 String 方法迁移到脚本实现

## Architecture

```
Surface:    s.Contains(t)                    ← 脚本方法（ordinary z42 method）
              ↓ compiles to
IR:         CallInstr String::Contains(...)  ← 普通 z42 函数调用
              ↓ Contains body (脚本)
IR:         BuiltinInstr __str_length        ← 最小核 intrinsic
            BuiltinInstr __str_char_at
            for loop + CompareChar (EqInstr)
VM:         Value::Str(String) 不变；新增 2 个 str + 3 个 char builtin
```

参考 C# CoreCLR：`src/libraries/System.Private.CoreLib/src/System/String.Searching.cs` 中 `Contains` 是 C# 代码，通过 `this[i]` + `Length` 循环实现；`string.Length` 是 `[Intrinsic]` property，JIT 特化为字段加载。

z42 的等价映射：
- `string.Length` 对应 C# `[Intrinsic]` property —— 我们保持 `BuiltinInstr("__str_length")` 作为"单条 IR 指令访问"，与 ldfld 等效
- `string[i]` 对应 `CharAt(int)` 方法调用 —— 本迭代不引入 `[]` 语法糖
- 其余方法都是 C# 代码循环 —— 对应 z42 脚本方法循环

## Decisions

### Decision 1: 用 `CharAt(int)` 而非引入 `s[i]` 语法糖

**问题：** C# 有 `s[i]` 索引器；z42 目前 `[]` 仅支持数组（`ArrayGetInstr`），String 未接入索引器。

**选项：**
- **A** — 本迭代加 `s[i]` 字符串索引：改 parser / typecheck / codegen，属于 lang 类变更
- **B** — 用显式 `s.CharAt(i)` 方法：纯 stdlib + 新增 extern

**决定：** 选 B。本迭代是 refactor/stdlib 重构，引入语法糖会扩大范围并触发完整 lang 变更流程。`CharAt` 在 C# / Java / Swift 都是官方 API，不丑陋。后续若确需 `[]` 语法糖，单独做 `add-string-indexer` 变更。

### Decision 2: Length 保持 `BuiltinInstr`，不引入专用 IR 指令

**问题：** 用户希望 "Length 是脚本字段而非 extern 函数"。

**选项：**
- **A** — 新增 `StrLenInstr(Dst, Src)` IR 指令；codegen 识别 `[Native("__str_length")]` 的返回位置并替换
- **B** — 保持 `BuiltinInstr("__str_length")`

**决定：** 选 B。理由：
1. `BuiltinInstr` 已经是单条 IR 指令 + 一次 hashmap 查找，和 `ldfld` 语义几乎等价（JIT 时可进一步特化）
2. 用户感知上 `s.Length` 已是 property 访问（非 `s.Length()` 方法调用），surface 层面已经"像字段"
3. 新增 IR 指令需要改 IR 序列化 / JIT / AOT 三处；收益小于改动面
4. 若后续性能 profile 显示 hot path，单独做 intrinsic inlining pass，不在本迭代冒险

### Decision 3: 索引语义统一到 Unicode scalar (char)

**问题：** 当前 `Length` 按 char 计数、`Substring` / `IndexOf` 按 byte 偏移，不一致。

**选项：**
- **A** — 统一按 Unicode scalar (char)
- **B** — 统一按 UTF-8 byte
- **C** — 保留两套 API（StringView / Span）

**决定：** 选 A。与 C# / Java 行为一致；用户心智简单；脚本实现容易（`CharAt(i)` 内部 `s.chars().nth(i)` 即可，O(n) 扫描，Phase 1 可接受）。B 是 Rust 风格，用户每次要处理 UTF-8 边界，学习成本高。C 引入 Span 概念对 Phase 1 过大。

### Decision 4: 简化 Unicode casing 为 ASCII 规则

**问题：** Unicode 大小写 locale-sensitive（Turkish I、German ß、Greek sigma）；完整支持需 ICU 数据。

**选项：**
- **A** — ASCII-only：`'A'..'Z' <-> 'a'..'z'`，其余返回自身
- **B** — Rust `char::to_lowercase()`：多码元支持（ß → ss），但复杂
- **C** — 接入 ICU / Unicode 表

**决定：** 选 A。Phase 1 stdlib 目标是"够用 + 纯净"，locale-sensitive casing 是独立大特性，待 L3 `CultureInfo` / `UnicodeTables` 时再做。文档明确标注限制。`IsWhiteSpace` 使用 Rust `char::is_whitespace()`（覆盖 ASCII 空白 + NBSP 等，行为稳定）。

### Decision 5: 保留 Split / Format / Join / Concat / Equals / CompareTo / Hash / ToString 为 extern

**问题：** 这些也能脚本化，是否本迭代一次做完？

**决定：** 保留。理由：
- `Split` 需要返回 `string[]`，涉及 dynamic array 构建，VM builtin 一次分配更高效
- `Format` 涉及变参 + 格式串解析 + object[] → string，脚本实现复杂（且需要 StringBuilder 成熟）
- `Equals` / `CompareTo` / `GetHashCode` 直接按 UTF-8 bytes 比较 / 哈希，脚本化无收益反而变慢
- `ToString` 是 identity，`BuiltinInstr` 直接返回自身即可，移到脚本反而啰嗦

本迭代目标是"删除 14 个明显可脚本化的 builtin"，这些复杂 / 性能敏感项待后续 StringBuilder / Formatter 成熟后独立迭代。

## Implementation Notes

### 新增 VM builtin（5 个）

文件 `src/runtime/src/corelib/string.rs`：
```rust
pub fn builtin_str_char_at(args: &[Value]) -> Result<Value> {
    let s = require_str(args, 0, "__str_char_at")?;
    let i = require_i64(args, 1, "__str_char_at")? as usize;
    s.chars().nth(i).map(Value::Char)
        .ok_or_else(|| anyhow!("CharAt index {} out of range (length {})", i, s.chars().count()))
}

pub fn builtin_str_from_chars(args: &[Value]) -> Result<Value> {
    let arr = require_char_array(args, 0, "__str_from_chars")?;
    Ok(Value::Str(arr.iter().collect::<String>()))
}
```

文件 `src/runtime/src/corelib/char.rs`（新建；或加到 `mod.rs` 相邻）：
```rust
pub fn builtin_char_is_whitespace(args: &[Value]) -> Result<Value> {
    let c = require_char(args, 0, "__char_is_whitespace")?;
    Ok(Value::Bool(c.is_whitespace()))
}

pub fn builtin_char_to_lower(args: &[Value]) -> Result<Value> {
    let c = require_char(args, 0, "__char_to_lower")?;
    Ok(Value::Char(if c.is_ascii_uppercase() { c.to_ascii_lowercase() } else { c }))
}

pub fn builtin_char_to_upper(args: &[Value]) -> Result<Value> {
    let c = require_char(args, 0, "__char_to_upper")?;
    Ok(Value::Char(if c.is_ascii_lowercase() { c.to_ascii_uppercase() } else { c }))
}
```

在 `corelib/mod.rs` dispatch 表注册 5 个新 builtin，反注册 13 条旧的。

### 删除 VM builtin（13 条，对应 11 个方法名）

`__str_contains` / `__str_starts_with` / `__str_ends_with` / `__str_index_of` / `__str_replace` / `__str_to_lower` / `__str_to_upper` / `__str_trim` / `__str_trim_start` / `__str_trim_end` / `__str_substring`（2 arity 共享一个 builtin 名）/ `__str_is_null_or_empty` / `__str_is_null_or_whitespace`

### 脚本实现要点（String.z42）

- `IsEmpty`：`Length == 0`（已有）
- `Contains(other)`：`return IndexOf(other) >= 0;`
- `StartsWith(prefix)`：长度检查 + for 循环 `this.CharAt(i) == prefix.CharAt(i)`
- `EndsWith(suffix)`：同上，从尾部起算 offset
- `IndexOf(other)`：朴素 two-pointer 扫描；外层 `i in 0..Length-other.Length+1`，内层 `j in 0..other.Length`
- `Replace(old, new)`：StringBuilder 模式 —— 扫描每个位置，命中则 append `new` 并跳过 `old.Length`，否则 append 单字符
- `Substring(start)` / `Substring(start, length)`：分配 `new char[length]` + `for` 循环 `result[i] = this.CharAt(start + i)` + `string.FromChars(result)`
- `ToLower()` / `ToUpper()`：分配 `new char[this.Length]` + 循环 `result[i] = this.CharAt(i).ToLower()` + `FromChars`
- `Trim()` / `TrimStart()` / `TrimEnd()`：先定位首/尾非空白索引，再 `Substring(s, e-s+1)`
- `IsNullOrEmpty(v)`：`v == null || v.Length == 0`
- `IsNullOrWhiteSpace(v)`：`null` 检查 + 全部 IsWhiteSpace 扫描

### Replace 的实现细节（最终决定：两遍扫描 + 固定 char[]）

**关键约束**：`List<T>` 位于 `z42.collections` 包，该包**依赖** `z42.core`（见 `z42.collections.z42.toml`）；因此 `z42.core/String.z42` **不能**反向 import `List<T>`，否则循环依赖。

**实现方式**：两遍扫描
1. **第一遍**：扫描 `this` 每个位置，命中 `oldValue` 计数 + 推进 `oldValue.Length`；否则推进 1。最终 `resultLen = this.Length + matchCount * (newValue.Length - oldValue.Length)`
2. **分配**：`char[] result = new char[resultLen]`
3. **第二遍**：再次扫描，命中则把 `newValue.CharAt(k)` 填入并跳过，否则把 `this.CharAt(i)` 填入。最后 `string.FromChars(result)`

时间 O(n·m)，空间 O(n + result)。

### Replace 空 oldValue 边界（最终：抛 Exception）

C# 在 `oldValue == ""` 时抛 `ArgumentException("String cannot be of zero length.")`。z42.core 无 `ArgumentException` 类型声明；延用 `Assert.z42` 已经在用的 `throw new Exception($"...")` 模式：

```z42
if (oldValue.Length == 0) {
    throw new Exception("Replace: oldValue cannot be empty");
}
```

后续若 z42 引入 `ArgumentException` 类型，再单独迭代升级。

### 测试策略

- **单元 / golden（C# 编译器）**：`src/compiler/z42.Tests/Goldens/StringScriptMethods/` 每个方法一个 golden source + expected IR（验证脚本 body 能正确编译）
- **端到端（VM）**：`src/runtime/tests/golden/run/88_string_script/` 覆盖每个重写方法的正常 + 边界场景（见 spec scenarios）
- **回归**：保留所有现有 string 相关 golden tests，确认行为等价（Substring / IndexOf 的 ASCII callers 应完全等价）
- **性能**：Phase 1 不作要求，文档备注 `Contains` / `IndexOf` 是 O(n*m) 朴素实现

## 回归风险

| 风险点 | 评估 | 缓解 |
|-------|------|-----|
| `Substring` / `IndexOf` byte → char 语义变更 | 低：现有 callers 全是 ASCII | 加 Unicode 测试验证新语义 |
| `Length` 已是 char 计数，无变化 | 无 | — |
| `ToLower` / `ToUpper` 语义：原 builtin 用 Rust `to_lowercase()`（Unicode-aware），新脚本 ASCII-only | 中：对非 ASCII 文本行为变化 | 文档标注；测试用 ASCII 场景；非 ASCII 留待 L3 |
| `Trim*` 空白判定：原 builtin Rust `trim()`（含 NBSP 等），新 `char::is_whitespace()` | 低：Rust `char::is_whitespace()` 与 `str::trim()` 等价 | — |
| `Replace` 空串 `old=""` 行为 | 边界：C# 抛 ArgumentException | 对齐 C#：`throw new Exception("Replace: oldValue cannot be empty")`（z42.core 无 ArgumentException 类型声明，用基础 Exception） |
