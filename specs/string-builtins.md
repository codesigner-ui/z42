# z42 字符串内置方法规范

## 设计参考

| 来源 | 借鉴点 |
|------|--------|
| **C#** | `string` 方法命名和签名（PascalCase，不可变语义）|
| **Rust** | `&str` 不可变；返回新字符串而非原地修改 |

---

## Phase 1 范围

只支持最常用的 6 个方法。其余（`Split`、`Replace`、正则等）留到 Phase 2。

| 方法 | 签名 | 返回类型 | 说明 |
|------|------|----------|------|
| `.Length` | `string.Length` | `int` | UTF-8 字节数 |
| `.Substring(start)` | `(int) → string` | `string` | 从 start 到末尾 |
| `.Substring(start, length)` | `(int, int) → string` | `string` | 子串 |
| `.Contains(value)` | `(string) → bool` | `bool` | 是否包含子串 |
| `.StartsWith(value)` | `(string) → bool` | `bool` | 是否以子串开头 |
| `.EndsWith(value)` | `(string) → bool` | `bool` | 是否以子串结尾 |

---

## 语法示例

```csharp
string s = "Hello, world!";
int n      = s.Length;               // 13
string sub = s.Substring(7);         // "world!"
string sub2 = s.Substring(7, 5);     // "world"
bool has   = s.Contains("world");    // true
bool sw    = s.StartsWith("Hello");  // true
bool ew    = s.EndsWith("!");        // true
```

---

## 语义

- 所有方法**不修改原字符串**（不可变语义，与 C#/Rust 一致）
- `Substring` 越界：运行时 panic（`index out of range`）
- `Length` 单位：UTF-8 字节数（与 Go 的 `len(s)` 一致，高性能且实现简单）

> **实现说明**：Phase 1 VM 的 `Value::Str` 存储 Rust `String`，`.len()` 直接得到 UTF-8 字节数，`Substring` 基于字节偏移（需对齐 UTF-8 边界，越界或未对齐时 panic）。这与 Go `len(s)` 语义一致，高性能。

---

## IR 映射

`.Length` 是属性（不是方法调用），但在 IR 层统一用 `builtin` 指令：

| 访问方式 | IR builtin name | 参数 | 返回 |
|----------|-----------------|------|------|
| `s.Length` | `__str_len` | `[s]` | `i32` |
| `s.Substring(start)` | `__str_substring` | `[s, start]` (2 args) | `str` |
| `s.Substring(start, len)` | `__str_substring` | `[s, start, len]` (3 args) | `str` |
| `s.Contains(v)` | `__str_contains` | `[s, v]` | `bool` |
| `s.StartsWith(v)` | `__str_starts_with` | `[s, v]` | `bool` |
| `s.EndsWith(v)` | `__str_ends_with` | `[s, v]` | `bool` |

`__str_substring` 通过参数个数区分：2 个参数（s, start）→ 到末尾；3 个参数（s, start, len）→ 指定长度。

---

## TypeChecker

- `.Length`：检查 receiver 类型为 `string`，结果类型 `int`
- `.Substring`：receiver 为 `string`，参数为 `int`，结果 `string`
- `.Contains` / `.StartsWith` / `.EndsWith`：receiver 和参数均为 `string`，结果 `bool`
- 方法名拼写错误：`error Z0401: undefined member 'xxx' on type 'string'`

---

## IrGen

`MemberCallExpr`（如 `s.Contains("x")`）在 IrGen 中识别 receiver 类型为 `string` 时，
直接生成对应 `BuiltinInstr`，而不走普通函数查找流程。

---

## 不在此规范范围内

- `ToUpper` / `ToLower` / `Trim` / `Replace` / `Split`（Phase 2）
- 字符串插值 `$"..."` 的运行时支持（已在 Phase 1 通过 IrGen 拼接实现）
- `string` 到 `char[]` 转换（Phase 2）
- 正则表达式（Phase 2）
