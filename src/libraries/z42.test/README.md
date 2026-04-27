# z42.test

z42 标准测试运行时（v0）—— 极简 imperative TestRunner，支撑 stdlib 自身和用户脚本的单元测试。

## 现状（v0, 2026-04-27）

z42 当前缺失三件支撑高级测试 API 的语言能力：

| 能力 | 状态 | 影响 v1+ API |
|---|---|---|
| Lambda / 函数引用 | L3 | `runner.Run("name", () => ...)` 等闭包式 API |
| 通用 Attribute（不只 [Native]）| L3+ | `[Test]` 注解 |
| Reflection / 类型枚举 | L3-R | 自动发现 `[Test]` 方法 |

所以 v0 用最朴素的 `Begin / try-catch-Fail / Summary` 模式。

## 使用

```z42
using Std.Test;

void Main() {
    var t = new TestRunner("MyTests");

    t.Begin("Addition");
    try { Assert.Equal(4, 2 + 2); } catch (Exception e) { t.Fail(e); }

    t.Begin("Concatenation");
    try { Assert.Equal("ab", "a" + "b"); } catch (Exception e) { t.Fail(e); }

    return t.Summary();   // exit code = failed 计数
}
```

输出（全过场景）：

```
══════════════════════════════════════
 MyTests
══════════════════════════════════════
  ✓ Addition
  ✓ Concatenation
──────────────────────────────────────
 Result: 2 passed, 0 failed
══════════════════════════════════════
```

## API

| 方法 | 说明 |
|---|---|
| `new TestRunner(string contextName)` | 打印 header（用 contextName） |
| `void Begin(string name)` | 开始一个 case；隐式 finalize 上一个（未 fail 视为 pass）|
| `void Fail(Exception e)` | 标记当前 case 失败 + 打印失败原因 |
| `int Summary()` | finalize 最后一个 case + 打印汇总 + 返回 failed 计数 |

## 路线图

### v1 — Lambda 就绪后

```z42
runner.Run("Addition", () => Assert.Equal(4, 2 + 2));
runner.Run("Concat",   () => Assert.Equal("ab", "a" + "b"));
return runner.Summary();
```

`Run` 自动 try/catch，省掉用户写 try/catch 的样板。Begin/Fail 仍保留作为底层 API。

### v2 — Attribute + Reflection 就绪后

```z42
public class MyTests {
    [Test] public void Addition() { Assert.Equal(4, 2 + 2); }
    [Test] public void Concat()   { Assert.Equal("ab", "a" + "b"); }
}

void Main() {
    return new TestRunner("MyTests").RunAll<MyTests>();
}
```

自动发现 `[Test]` 注解的方法 + 实例化 + 调用。与 xUnit / NUnit 风格对齐。

## 设计选择

- **不依赖 native 库** — 纯脚本，可被 stdlib 自身用（一旦 z42 编译速度允许 stdlib 互测）
- **不引入新 builtin** — 复用 `Std.Assert.*` + `Console.WriteLine` + `Exception.Message`
- **Summary 返回 int** — 适合做 `Main()` 的返回值传给 process exit code
- **Begin 是显式动词** —— 故意与 xUnit `[Fact]`/JUnit 隐式风格不同；v0 用户必须手写每个 case 的开始

## 不做（明确否决）

- ❌ 自定义 Assert 类 —— 复用 `Std.Assert`，不重复发明
- ❌ 异步测试支持 —— 等 L3 async/await
- ❌ 参数化测试 —— 等 lambda + collection literals
- ❌ 测试发现 / 自动注册 —— 等 reflection
