# z42 语言定制化设计

## 设计哲学

z42 的编译器遵循一个核心原则：

> **代码提供机制（mechanism），配置提供策略（policy）。**

这意味着：
- 编译器核心代码只负责"如何解析/检查/生成"
- "解析什么"、"允许哪些特性"、"优先级如何"由**数据驱动**
- 添加新运算符、禁用某个语句、调整优先级，只需修改配置表，无需改动解析逻辑

这使 z42 既是一门完整的系统编程语言，也是一个高度可定制的嵌入式脚本引擎。

---

## 配置层

### 1. LanguageFeatures — 特性开关

`LanguageFeatures`（[Features/LanguageFeatures.cs](../../src/compiler/z42.Compiler/Features/LanguageFeatures.cs)）是最高层的定制点。每个特性用 snake_case 字符串标识，值为 `true`（启用）或 `false`（禁用）。

```csharp
// 只允许基础算术 + 控制流，禁用面向对象和异常
var restricted = LanguageFeatures.Minimal.WithOverrides(new Dictionary<string, bool>
{
    ["control_flow"] = true,
    ["oop"]          = false,
    ["exceptions"]   = false,
});
```

**内置预设**：

| 预设 | 用途 |
|------|------|
| `LanguageFeatures.Minimal` | Hello World 最小子集 |
| `LanguageFeatures.Phase1`  | 完整 C# 12 子集（默认）|

**已知特性名**（见 `KnownFeatureNames`）：

| 特性名 | 控制内容 |
|--------|---------|
| `control_flow` | `if` / `while` / `do` / `for` / `foreach` / `break` / `continue` |
| `exceptions`   | `try` / `catch` / `finally` / `throw` |
| `pattern_match`| `switch` 语句和表达式 |
| `oop`          | `class` / `interface` / `struct` / `record` / `new` |
| `arrays`       | 数组创建和索引 |
| `bitwise`      | `&`, `\|`, `^`, `~`, `<<`, `>>` 及对应复合赋值 |
| `null_coalesce`| `??` 运算符 |
| `ternary`      | `? :` 三元运算符 |
| `cast`         | `(Type)expr` 强制转型 |
| `lambda`       | Lambda 表达式 `=>` |
| `async`        | `async` / `await` |
| `nullable`     | `T?` 可空类型 |
| `interpolated_str` | `$"..."` 插值字符串 |
| `tuples`       | 元组类型和字面量 |
| `delegates`    | 委托类型 |
| `reflection`   | `typeof` / `nameof` |
| `threading`    | `lock` / `Interlocked` |
| `using_stmt`   | `using` 语句（资源管理）|

---

### 2. ParseTable — 运算符与语句表

`ParseTable`（[Parser/ParseTable.cs](../../src/compiler/z42.Compiler/Parser/ParseTable.cs)）是运算符优先级和特性门控的**单一数据源**（Single Source of Truth）。

#### ExprRules — 表达式运算符表

每条记录是 `ParseRule(LeftBp, Nud, Led, Feature?)`：

| 字段 | 含义 |
|------|------|
| `LeftBp` | 作为中缀运算符时的绑定力；0 = 纯前缀 |
| `Nud`    | 前缀 / 原子处理函数（null = 不能作前缀）|
| `Led`    | 中缀 / 后缀处理函数（null = 不能作中缀）|
| `Feature`| 关联的特性名；null = 始终启用 |

**绑定力等级**（间隔 10，方便插入新级别）：

```
10  赋值          (右结合)
20  三元 / ??
30  逻辑或  ||
40  逻辑与  &&
44  按位或  |      [feat:bitwise]
46  按位异或 ^     [feat:bitwise]
48  按位与  &      [feat:bitwise]
50  相等    == !=
60  比较    < <= > >= is as
65  移位    << >>  [feat:bitwise]
70  加减    + -
80  乘除余  * / %
90  后缀    () . [] ++ --
```

**新增运算符只需一行**：

```csharp
// 在 ParseTable.cs 的 ExprRules 中加一行，再在 Leds.cs 中加对应的 handler
[TokenKind.StarStar] = new(75, null, Leds.BinaryLeft("**", 75)),
```

#### StmtRules — 语句关键字表

每条记录是 `StmtRule(Fn, Feature?)`，与 `LanguageFeatures` 中的特性名一一对应：

```csharp
[TokenKind.For]    = new(Stmts.For_,    "control_flow"),
[TokenKind.Try]    = new(Stmts.TryCatch_, "exceptions"),
[TokenKind.Switch] = new(Stmts.Switch_,  "pattern_match"),
```

**新增语句关键字只需两步**：
1. 在 `StmtRules` 加一行，指定 handler 函数和可选特性名
2. 在 `Stmts.cs` 实现对应的 `StmtFn` 委托

---

### 3. Nud / Led / Stmts — 处理函数

运算符和语句的**具体解析逻辑**位于：

| 文件 | 内容 |
|------|------|
| [Nuds.cs](../../src/compiler/z42.Compiler/Parser/Nuds.cs) | 所有前缀 / 原子处理函数 |
| [Leds.cs](../../src/compiler/z42.Compiler/Parser/Leds.cs) | 所有中缀 / 后缀处理函数 |
| [Stmts.cs](../../src/compiler/z42.Compiler/Parser/Stmts.cs) | 所有语句处理函数 |

这些文件中的函数是无状态的，仅依赖 `ParserContext`，可以独立测试。

---

## 典型定制场景

### 场景一：嵌入式脚本引擎（游戏逻辑脚本）

只允许算术、控制流、函数调用，禁用面向对象、异常处理和多线程：

```csharp
var scriptFeatures = LanguageFeatures.Minimal.WithOverrides(new()
{
    ["control_flow"] = true,
    ["arrays"]       = true,
    ["interpolated_str"] = true,
});

var compiler = new Compiler(scriptFeatures);
```

### 场景二：教学环境（逐步引入特性）

第一周只教基本类型和输出，第二周引入控制流，第三周引入 OOP：

```csharp
// 第一周
var week1 = LanguageFeatures.Minimal;
// 第二周
var week2 = week1.WithOverrides(new() { ["control_flow"] = true });
// 第三周
var week3 = week2.WithOverrides(new() { ["oop"] = true });
```

### 场景三：新运算符实验

在不影响现有代码的情况下，实验性地添加一个 `**`（幂运算）运算符：

```csharp
// ParseTable.cs — 加一行
[TokenKind.StarStar] = new(75, null, Leds.BinaryLeft("**", 75), "pow_operator"),

// LanguageFeatures — 加一行到 Phase1 和 Minimal
["pow_operator"] = false,   // Minimal 中默认禁用
["pow_operator"] = true,    // Phase1 中默认启用

// Leds.cs — 已有 BinaryLeft 泛型，无需新代码
```

---

## 设计约束

- **特性名必须是 snake_case**，与 `ParseTable` 中的 `Feature` 字段和 grammar 注释中的 `[feat:NAME]` 标签保持一致
- **新增特性时**，同时在 `Minimal` 和 `Phase1` 两个预设中声明（`Minimal` 中默认 `false`，`Phase1` 中根据语义决定）
- **`GrammarSyncTests`** 验证 `ParseTable` 中所有非 null 的 `Feature` 字段都在 `LanguageFeatures.KnownFeatureNames` 中存在，防止拼写错误的特性名静默失效

---

## 与同类方案的比较

| 方案 | 运行时特性门控 | 配置即数据 | 可作嵌入引擎 |
|------|-------------|-----------|------------|
| **z42 ParseTable** | ✅ | ✅ | ✅ |
| pest (.pest 文件) | ❌ 编译时静态 | ✅ 语法即文档 | ❌ |
| nom (Rust combinator) | ❌ | ❌ 规则即代码 | ❌ |
| Roslyn scripting API | ❌ | ❌ | ✅ 但不可裁剪 |

z42 的核心差异在于**运行时特性门控**：同一套编译器代码，通过传入不同的 `LanguageFeatures` 实例，即可得到行为完全不同的语言方言。
