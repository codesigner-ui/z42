# Proposal: Extend z42.test Library — TestIO + Bencher + Assert ext (R2 完整版)

> **重要**：本文件 2026-05-05 完全重写。原 9 阶段 DRAFT 中 attribute 类型 / TestFailure / minimal Assert 已经分别由 R1.C / R4.A / R4.B / R2 minimal 落地；本版仅覆盖剩余的 lambda 依赖项。

## Why

z42.test 当前给出的能力（R2 minimal）：

```
[Test] [Skip] [Ignore] [Setup] [Teardown] [Benchmark] [ShouldThrow<E>]   ← R1.C / R4.A / R4.B
TestFailure / SkipSignal                                                  ← R2 minimal
Assert.Equal / NotEqual / True / False / Null / NotNull / Contains /     ← R2 minimal
       Fail / Skip                                                        (9 方法)
```

库源码自身留了三处显式 TODO（受 closure 阻塞）：

- [src/libraries/z42.test/src/Assert.z42:21-24](src/libraries/z42.test/src/Assert.z42) — `Throws<E>` / `DoesNotThrow` / `Near`
- [src/libraries/z42.test/src/TestRunner.z42:5-22](src/libraries/z42.test/src/TestRunner.z42) — "v1（lambda 就绪后）规划 API"
- 缺 `TestIO.captureStdout` 让用户能在测试里捕获被测代码的输出
- 缺 `Bencher` 让 `[Benchmark]` 函数有可组合 API

2026-05-02 lambda + delegate 全管线落地（`Action` / `Func<...>` / closure capture），上述阻塞解除。本 spec 把这些 TODO 兑现，让 z42.test 成为 lambda 时代下完整、闭环的测试库。

## What Changes

### Phase A — corelib stdout/stderr sink dispatch（Rust）

[src/runtime/src/corelib/io.rs](src/runtime/src/corelib/io.rs) 的 `builtin_println` / `builtin_print` 当前直接写 `println!` / `print!`。增加 thread-local sink：

- 安装时 → 写到 `Vec<u8>` buffer
- 未安装 → 走原 stdout（行为不变）

stderr 同理。两个 sink 独立 install / take。

### Phase B — TestIO（z42.test 库）

新文件 `src/libraries/z42.test/src/TestIO.z42`：

```z42
public static class TestIO {
    public static string captureStdout(Action body);
    public static string captureStderr(Action body);
    public static CaptureResult captureBoth(Action body);
}

public class CaptureResult {
    public string Stdout { get; }
    public string Stderr { get; }
}
```

实现：install sink → invoke `body()` → take buffer → return string；任一异常仍卸载 sink（finally）。

### Phase C — Bencher（z42.test 库 + 2 个 corelib helpers）

corelib 增 `__bench_now_ns` / `__bench_black_box`（thin wrappers，no-op for black_box in interp，对 jit 留挂钩点）。

新文件 `src/libraries/z42.test/src/Bencher.z42`：

```z42
public class Bencher {
    public Bencher();                              // 默认 warmup=10, samples=100
    public Bencher(int warmupIters, int sampleIters);

    public void iter(Action body);                 // 跑 warmup + samples 次

    public long MinNs { get; }
    public long MaxNs { get; }
    public long MedianNs { get; }
    public long TotalNs { get; }
    public int  Samples { get; }

    public void printSummary(string label);        // 人类可读输出
}

public static class BenchHelpers {
    public static T blackBox<T>(T value);          // 防 JIT 优化死代码消除
}
```

Bencher 仅做"测一段代码 N 次，记录纳秒分布"。Runner-side `[Benchmark]` 调度（实际跑这些方法、聚合 N 个 module 的输出、JSON / criterion-baseline diff）**不在本 spec scope** —— 用户可以在 `[Test]` 里手动 `var b = new Bencher(); b.iter(() => ...); b.printSummary("foo");` 跑出来，输出走 stdout。

### Phase D — Assert 扩展

[src/libraries/z42.test/src/Assert.z42](src/libraries/z42.test/src/Assert.z42) 加：

```z42
public static void Throws<E>(Action body) where E : Exception;
public static void DoesNotThrow(Action body);
public static void EqualApprox(double actual, double expected, double eps);
```

`Throws<E>` 与 `[ShouldThrow<E>]` 是互补关系（前者断言"这一段代码"抛 E，后者断言"整个测试方法"抛 E）。

### Phase E — Validator E0912 完整化

R4.A 留下：

```csharp
// First-parameter-is-Bencher check pending R2.C (closure dependency).
```

实际不是 closure 阻塞，是 Bencher type 还不存在。本 spec 创建后该检查可以补上：`[Benchmark] void Foo(Bencher b)` —— 第一参数必须是 `Bencher`。

### Phase F — dogfood + 库自检测试

更新 [src/libraries/z42.test/tests/dogfood.z42](src/libraries/z42.test/tests/dogfood.z42) 加 ~6 个新测试：

- `TestIO.captureStdout` 捕获 `Console.WriteLine` 输出
- `TestIO.captureStderr` 捕获 stderr
- `TestIO.captureBoth` 同时捕获
- `Assert.Throws<TestFailure>` 命中
- `Assert.DoesNotThrow` 通过路径
- `Assert.EqualApprox` 浮点容差通过 / 失败
- 一个简短 Bencher 演示（assert MedianNs > 0 + printSummary 的 capture）

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/io.rs` | MODIFY | thread-local stdout/stderr sink + 4 个新 native helpers |
| `src/runtime/src/corelib/io_tests.rs` | NEW | sink install/take/restore 行为单元测试 |
| `src/runtime/src/corelib/bench.rs` | NEW | `__bench_now_ns` + `__bench_black_box` |
| `src/runtime/src/corelib/bench_tests.rs` | NEW | 单调时钟、blackBox 透传 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 6 个新 native（4 testio + 2 bench） |
| `src/libraries/z42.test/src/TestIO.z42` | NEW | TestIO 类 + CaptureResult |
| `src/libraries/z42.test/src/Bencher.z42` | NEW | Bencher 类 + BenchHelpers.blackBox |
| `src/libraries/z42.test/src/Assert.z42` | MODIFY | 加 Throws<E> / DoesNotThrow / EqualApprox |
| `src/libraries/z42.test/z42.test.toml` | MODIFY | 暴露新 public types |
| `src/libraries/z42.test/tests/dogfood.z42` | MODIFY | 加 ~7 个新 [Test] |
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | MODIFY | E0912 完整化（first-param-is-Bencher） |
| `src/compiler/z42.Tests/TestAttributeTests.cs` | MODIFY | 加 1-2 个 E0912 case |
| `docs/design/testing.md` | MODIFY | "TestIO" + "Bencher" 章节 |
| `docs/roadmap.md` | MODIFY | M6 R2 完整版完成条目 |

**只读引用**：

- `src/libraries/z42.core/src/Delegates/Delegates.z42` — `Action` / `Func<T,R>` 类型定义
- `src/runtime/src/corelib/mod.rs::NativeFn` 签名约定
- `src/compiler/z42.Semantics/TestAttributeValidator.cs::ValidateBenchmarkPartialSignature`
- `src/libraries/z42.test/src/Failure.z42` — TestFailure / SkipSignal

## Out of Scope

- ⏸️ **Runner [Benchmark] 调度模式**：runner 当前 `entry.kind != TestEntryKind::Test { continue; }`；本 spec **不**让 runner 跑 [Benchmark] 函数。用户在 `[Test]` 里手动用 Bencher 即可达到目的。Runner integration 留给独立 spec
- ⏸️ **JSON / criterion-style baseline diff**：bench 输出当前只人类可读
- ⏸️ **`[BenchmarkParam(value)]`** 参数化基准
- ⏸️ **CI 集成 / GitHub Actions 跑 bench**：本 spec 不动 CI；z42 用户代码层（test-vm / test-stdlib / test-cross-zpkg）的 CI 是独立后续工作；compiler 侧（dotnet test / `z42.Bench` BDN）的 CI 投入暂缓到 z42 自举完成（按用户 2026-05-05 指示）
- ⏸️ **TestRunner.z42 v1 重写**（[TestRunner.z42:22] 列出的"lambda 就绪后规划 API"）：保留为独立小 spec；本 spec 聚焦库 API 补齐
- ⏸️ **stdout 捕获嵌套**：单层够用；嵌套调用 `captureStdout` 行为是上层 capture 看到下层调用结果（与 install sink 的 stack 行为一致；不刻意设计）

## Open Questions

- [ ] `Bencher.iter` 接 `Action`（无返回）还是 `Func<R>`（有返回让用户能 `Assert.Equal(b.iter(...), expected)`）？预设：**Action**（criterion / cargo-bench 也是；返回值用闭包捕获到外部变量即可）
- [ ] `BenchHelpers.blackBox<T>` 是否真的需要泛型？interp 端没有 dead-code elimination；JIT 端 cranelift 也未来才需要。**预设：保留泛型签名**（API 形态稳定，实现端 no-op 直到必要）
- [ ] sink 行为：`captureBoth` 调用期间，user code 的 `Console.WriteLine` 是同时写 stdout buffer + stderr buffer，还是只 stdout？**预设：仅按调用的 channel 分流**（println → stdout buffer，Console.Error.WriteLine → stderr buffer），与 OS 行为一致
