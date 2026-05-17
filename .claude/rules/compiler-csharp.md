---
paths:
  - "src/compiler/**/*.cs"
---

# C# 编译器开发规范

## AST 节点

- 所有 AST 节点必须是 `sealed record`，继承对应的抽象基类（`Item`、`Expr`、`Stmt`、`TypeExpr`、`Pattern`）
- 每个节点末位参数必须是 `Span Span`，用于错误报告
- 节点不包含语义信息（类型、作用域等），这些由 TypeCheck 阶段填充

## TokenKind

- 新增关键字时，在 `TokenDefs.cs` 的 `Keywords` 字典注册；新增符号在 `Lexer.cs` 符号 switch 中处理
- 关键字用 `TokenKind.Xxx`（PascalCase），符号用描述名（`LtEq`, `FatArrow` 等）
- `Underscore` 是独立 token，不要把 `_` 识别为 identifier

## Parser 架构（Superpower 风格，手写组合子）

**不引入外部 parser combinator 库**（为自举保留）；参考 Datalust/Superpower 设计思路手写实现。

### 核心类型（`Parser/Core/`）

| 类型 | 职责 |
|------|------|
| `TokenCursor` (readonly struct) | 不可变游标；`Advance/Peek` 返回新游标，lookahead 直接用游标而非 `_pos+i` 算术 |
| `ParseResult<T>` (readonly struct) | 成功: `Value + Remainder`；失败: `Error + ErrorSpan`；`.Unwrap(ref cursor)` 升级为异常 |
| `Parser<T>` (delegate) | `TokenCursor → ParseResult<T>` 纯函数委托 |
| `Combinators` (static class) | 组合子：`Token / Or / Many / Optional / SeparatedBy / Between / Then / Select / Lazy` |

### 解析策略

- **递归下降 + Pratt 表达式**：顶层声明和语句用递归下降，表达式用 Pratt（NudTable / LedTable）
- **可恢复失败**：`Or` 分支检查 `ParseResult<T>.IsOk`，失败不消耗游标
- **不可恢复错误**：`.OrThrow()` / `.Unwrap(ref cursor)` 将失败升级为 `ParseException`
- **Feature gate**：`feat.IsEnabled("xxx")`；未启用时 `throw new ParseException(...)`
- **不吞错误**：expression 解析失败必须 throw，不做 panic-mode recovery

### 运算符优先级（Pratt binding power）

```
10  赋值（right-assoc）        20  三目 / ??
30  ||                          40  &&
44/46/48  |/^/& (bitwise)      50  == !=
60  < <= > >= is as            65  << >> (bitwise)
70  + -                        80  * / %
85  switch expr (postfix)      90  ++ -- call . [] (postfix)
```

### 文件职责

| 文件 | 职责 |
|------|------|
| `Core/TokenCursor.cs` | 不可变游标 |
| `Core/ParseResult.cs` | 结果类型 + `Unwrap(ref cursor)` |
| `Core/Combinators.cs` | 组合子（`Parser<T>` delegate + 扩展方法） |
| `TypeParser.cs` | 类型表达式，暴露 `Parser<TypeExpr> TypeExpr` 属性供组合子使用 |
| `ExprParser.cs` | Pratt loop + NudTable + LedTable（NudFn/LedFn 显式传 cursor） |
| `StmtParser.cs` | 语句解析（含 `ParseBlock`，返回 `ParseResult<BlockStmt>`） |
| `TopLevelParser.cs` | 顶层声明；lookahead 用 `cursor.SkipWhile(...)` 而非 `_pos+i` |
| `Parser.cs` | 公开入口 + `ParseException` 定义 |

## 错误消息

- 格式：`` expected `(` but got `{token}` ``
- 面向语言使用者，不用内部枚举名（`` expected `(` `` 而非 `expected LParen`）

## Lexer 架构（规则驱动，分层组合子）

### 分层结构

```
LexCombinators (Char/Lit/Many/Opt/Seq/Or)  Lexer/LexCombinators.cs
        ↓ 组合成
Token rules (SymbolRules/NumericRules/      TokenDefs.cs
             StringRules/Keywords)
        ↓ 执行
Lexer (通用引擎，无业务 if-else)             Lexer.cs
```

### 文件职责

| 文件 | 职责 |
|------|------|
| `Lexer/LexCombinators.cs` | `LexRule = (string, int) → int?` 委托 + 组合子 |
| `Lexer/TokenDefs.cs` | 所有规则声明：Keywords / TypeKeywords / SymbolRules / NumericRules / StringRules / Display |
| `Lexer/Lexer.cs` | 通用执行引擎，无业务规则 |

### 新增规则说明

| 新增什么 | 只改 TokenDefs.cs |
|----------|-------------------|
| 新关键字 | `Keywords` 加一行 |
| 新符号运算符 | `SymbolRules` 加一行（自动最长匹配） |
| 新数字格式（如八进制） | `NumericRules` 加一行组合子规则 |
| 新字符串前缀（如 `@"`) | `StringRules` 加一行 |

### LexCombinators 核心组合子

`TokenDefs.cs` 通过 `using static Z42.Compiler.Lexer.LexCombinators;` 导入，直接调用：

```csharp
Char(pred)          // 匹配单个满足条件的字符
Lit(s) / LitI(s)   // 匹配字面量（大小写敏感/不敏感）
OneOf(chars)        // 匹配多选一字符
Many(pred, sep)     // 零或多（支持 _ 分隔符）
Many1(pred, sep)    // 一或多
Opt(rule)           // 可选（始终成功）
Seq(rules...)       // 顺序组合
Or(rules...)        // 有序选择（第一个匹配）
```

## 资源加载顺序必须显式排序（2026-05-17 强化）

**任何"first-wins 注册到全局 key"的资源加载循环都必须先 `.OrderBy(...)` 才迭代，禁止依赖 OS / 文件系统 / Dict / HashSet 的"碰巧 alphabetical"。**

### 为什么这条规则

`Directory.EnumerateFiles` **不是** alphabetical：
- macOS APFS：通常按字母序（巧合）
- Linux ext4 / btrfs：按 inode 顺序（与创建顺序相关）
- Windows NTFS：通常按字母序，但 .NET runtime 版本 + 文件系统驱动会影响

`HashSet<T>` / `Dictionary<TKey, TValue>` 迭代顺序：
- 取决于内部 bucket 布局 + string hash
- .NET 5+ 默认开启 string hash randomization → **每个进程的迭代顺序都可能不同**

任何后续依赖"第一个出现的赢" 的 first-wins 逻辑（`TryAdd` / `if (!dict.ContainsKey)` / 类似 pattern）一旦上面任何一个非确定性源进入数据流，**整条解析链都变成非确定性的**。本地某 OS 上"碰巧 alphabetical"会让人误以为正确，CI 在另一 OS 上炸。

### 现场案例（2026-05-17 fix-depindex-nondeterministic-order）

`PackageCompiler.BuildDepIndex` 用 `Directory.EnumerateFiles(dir, "*.zpkg")` 迭代 zpkg 路径 → `DependencyIndex.Build` 用 `TryAdd` 注册 `<ShortClass>.<Method>` 静态 key。z42.core 的 `Std.Assert.Equal` 和 z42.test 的 `Std.Test.Assert.Equal` 都注册到同一个 key `"Assert.Equal"`，谁先到谁赢。

- macOS：z42.core 字母序在前 → 用户写 `Assert.Equal(1, 2)` emit 到 `Std.Assert.Equal` ✓
- Linux/Windows CI：枚举顺序不同 → emit 到 `Std.Test.Assert.Equal` ✗ → zbc 5 字节漂移 + 测试输出从 "AssertionError" 变成 "values not equal"

### 强制规则

写任何"加载 zpkg / 加载 module / 加载 plugin / 注册 builtin"循环时：

1. **加载循环前必须 `.OrderBy(stableKey, StringComparer.Ordinal).ToList()` 一次**
   - 顺序键要语义稳定（prelude-first → 字母序，或纯字母序）
   - 不能用文件系统 mtime / inode / hash code 作为排序键

2. **不要"碰巧 alphabetical"**：如果某次本地测试通过但你怀疑只是巧合，就显式 `OrderBy` 一次（成本几乎为零）

3. **现有 `foreach (var x in hashSet)` / `foreach (var x in dict.Values)` + first-wins 写入** 都是潜在 bug 点 — 见到就加 `OrderBy`

### 反例（曾经出过 bug 的代码）

```csharp
// ❌ Linux/Windows 顺序不确定
foreach (var zpkgPath in Directory.EnumerateFiles(dir, "*.zpkg"))
{
    // first-wins 注册
    staticBuf.TryAdd(staticKey, entry);
}

// ❌ HashSet 迭代顺序不确定
foreach (var path in allPaths)
    foreach (var mod in LoadZpkg(path))
        modules.Add(mod);
```

### 正例

```csharp
// ✅ 显式排序 + prelude-first 语义键
var sortedPaths = Directory.EnumerateFiles(dir, "*.zpkg")
    .OrderBy(p => {
        string name = Path.GetFileNameWithoutExtension(p);
        return PreludePackages.Names.Contains(name) ? "0_" + name : "1_" + name;
    }, StringComparer.Ordinal);
foreach (var zpkgPath in sortedPaths) { ... }

// ✅ HashSet 迭代前显式排
foreach (var path in allPaths.OrderBy(p => p, StringComparer.Ordinal))
    foreach (var mod in LoadZpkg(path))
        modules.Add(mod);
```
