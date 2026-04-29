# Design: Extend z42.test Library

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  用户测试文件 (.z42)                                    │
│                                                         │
│  import z42.test.{Test, Assert, TestIO};                │
│                                                         │
│  [Test]                                                 │
│  fn test_x() {                                          │
│      let captured = TestIO.captureStdout(|| {           │
│          Console.println("hello");                      │
│      });                                                │
│      Assert.eq(captured, "hello\n");                    │
│  }                                                      │
└────────────────────────┬────────────────────────────────┘
                         │ uses
                         ▼
┌─────────────────────────────────────────────────────────┐
│  z42.test 库 (.z42 源码)                                │
│                                                         │
│  Test.z42       — [Test] / [Skip] / ... attribute types │
│  Assert.z42     — eq / throws / near / ...              │
│  TestIO.z42     — captureStdout (calls native helpers)  │
│  Bencher.z42    — iter / black_box                      │
│  Failure.z42    — TestFailure exception                 │
└────────────────────────┬────────────────────────────────┘
                         │ native interop
                         ▼
┌─────────────────────────────────────────────────────────┐
│  Rust 端 native helpers                                 │
│                                                         │
│  corelib/test_io.rs                                     │
│    __test_io_install_stdout_sink()                      │
│    __test_io_take_stdout_buffer() -> string             │
│                                                         │
│  corelib/bench.rs                                       │
│    __bench_now_ns() -> u64                              │
│    __bench_black_box<T>(v: T) -> T                      │
│                                                         │
│  corelib/io.rs (modified)                               │
│    println: check sink thread-local before stdout       │
└─────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: Attribute 类型（`Test.z42`）

```z42
namespace z42.test;

// Mark a function as a test case.
public attribute Test;

// Mark a function as a benchmark.
public attribute Benchmark;

// Skip a test (still listed; status = skipped).
public attribute Skip {
    reason: string;
}

// Permanently ignore (not listed in any output).
public attribute Ignore;

// Expect the test body to throw a specific exception type.
public attribute ShouldThrow<E: Exception>;

// Setup hook: runs before each [Test] in the same module.
public attribute Setup;

// Teardown hook: runs after each [Test] in the same module.
public attribute Teardown;
```

`TestCase.z42` 单独：

```z42
namespace z42.test;

// Run the test once per parameter set.
//   [TestCase(0, 0)] [TestCase(1, 1)] [TestCase(10, 55)]
//   fn test_fib(n: i32, expected: i32) { ... }
public attribute TestCase {
    args: object[];   // tuple of values; runner deserializes per call
}
```

`Benchmark.z42`：

```z42
namespace z42.test;

public attribute Benchmark;

public attribute BenchmarkParam {
    value: object;    // single param; multiple [BenchmarkParam] = multiple variants
}
```

> **注**：z42 attribute 系统当前形态需调研。本 spec 假定支持 `attribute X { ... }` 声明 + `[X(args)]` 使用。如不完全支持，本 spec 会在 R4（语义校验）时一并完善。

### Decision 2: TestFailure 类（`Failure.z42`）

```z42
namespace z42.test;

public class TestFailure : Exception {
    public actual:   string;
    public expected: string;
    public location: string;    // file:line, captured at Assert call site
    public message:  string;

    public ctor(message: string, actual: string = "", expected: string = "", location: string = "") {
        this.message  = message;
        this.actual   = actual;
        this.expected = expected;
        this.location = location;
    }
}
```

### Decision 3: Assert API（按语义分组）

`Assert.z42`：

```z42
namespace z42.test;

public class Assert {
    // ── 5.1 相等 ─────────────────────────────────────
    public static fn eq<T: Equatable>(actual: T, expected: T) -> void {
        if (actual != expected) {
            throw TestFailure(
                message:  "values not equal",
                actual:   __value_to_string(actual),
                expected: __value_to_string(expected)
            );
        }
    }

    public static fn notEq<T: Equatable>(actual: T, expected: T) -> void { /* ... */ }
    public static fn same(actual: object, expected: object) -> void { /* ref equality */ }
    public static fn notSame(actual: object, expected: object) -> void { /* ... */ }

    // ── 5.2 布尔 ─────────────────────────────────────
    public static fn isTrue(value: bool) -> void {
        if (!value) throw TestFailure("expected true, got false");
    }
    public static fn isFalse(value: bool) -> void { /* ... */ }
    public static fn isNull<T>(value: T?) -> void { /* ... */ }
    public static fn isNotNull<T>(value: T?) -> void { /* ... */ }

    // ── 5.3 浮点 ─────────────────────────────────────
    public static fn near(actual: f64, expected: f64, epsilon: f64 = 1.0e-9) -> void {
        let delta = if (actual >= expected) actual - expected else expected - actual;
        if (delta > epsilon) {
            throw TestFailure(
                message:  "values not within epsilon",
                actual:   __value_to_string(actual),
                expected: __value_to_string(expected) + " ± " + __value_to_string(epsilon)
            );
        }
    }
    public static fn relativeNear(actual: f64, expected: f64, relTol: f64 = 1.0e-7) -> void { /* ... */ }
    public static fn isFinite(value: f64) -> void { /* ... */ }
    public static fn isNaN(value: f64) -> void { /* ... */ }

    // ── 5.4 集合 ─────────────────────────────────────
    public static fn contains<T: Equatable>(collection: iterable<T>, item: T) -> void { /* ... */ }
    public static fn notContains<T: Equatable>(collection: iterable<T>, item: T) -> void { /* ... */ }
    public static fn empty<T>(collection: iterable<T>) -> void { /* ... */ }
    public static fn notEmpty<T>(collection: iterable<T>) -> void { /* ... */ }
    public static fn lengthEq<T>(collection: iterable<T>, expected: usize) -> void { /* ... */ }
    public static fn elementsEqual<T: Equatable>(actual: iterable<T>, expected: iterable<T>) -> void { /* ... */ }
    public static fn elementsEquivalent<T: Equatable + Hashable>(actual: iterable<T>, expected: iterable<T>) -> void { /* ... */ }

    // ── 5.5 字符串 ────────────────────────────────────
    public static fn startsWith(actual: string, prefix: string) -> void { /* ... */ }
    public static fn endsWith(actual: string, suffix: string) -> void { /* ... */ }
    public static fn matchesRegex(actual: string, pattern: string) -> void { /* ... */ }
    public static fn containsSubstring(actual: string, sub: string) -> void { /* ... */ }

    // ── 5.6 异常 ─────────────────────────────────────
    public static fn throws<E: Exception>(action: fn() -> void) -> E {
        try {
            action();
        } catch (e: E) {
            return e;
        } catch (other: Exception) {
            throw TestFailure(
                message:  "expected exception of different type",
                actual:   other.GetType().Name,
                expected: "<E>"
            );
        }
        throw TestFailure(message: "expected exception was not thrown", expected: "<E>");
    }
    public static fn throwsWithMessage<E: Exception>(action: fn() -> void, messageContains: string) -> E { /* ... */ }
    public static fn doesNotThrow(action: fn() -> void) -> void { /* ... */ }

    // ── 5.7 主动控制 ─────────────────────────────────
    public static fn fail(message: string) -> never {
        throw TestFailure(message: message);
    }
    public static fn skip(reason: string) -> never {
        throw SkipSignal(reason: reason);   // runner catches SkipSignal as "skipped" status
    }
}

public class SkipSignal : Exception {
    public reason: string;
    public ctor(reason: string) { this.reason = reason; }
}
```

> **泛型约束 `<T: Equatable>` 等**：z42 generics 在 L3。R2 实施时若泛型未就绪，先用 `<T>` (无约束) + 运行时 `==` 操作符实现，等 L3 落地后回归补约束。这是临时妥协，design.md 在实施时记录。

### Decision 4: TestIO 类（`TestIO.z42`）

```z42
namespace z42.test;

public class TestIO {
    // Capture stdout written during action(), return as string.
    // Nested captures: inner takes priority; outer resumes after inner returns.
    public static fn captureStdout(action: fn() -> void) -> string {
        __test_io_install_stdout_sink();
        try {
            action();
            return __test_io_take_stdout_buffer();
        } catch (e: Exception) {
            // Take buffer to avoid leak even on exception, then re-throw.
            let _ = __test_io_take_stdout_buffer();
            throw;
        }
    }

    public static fn captureStderr(action: fn() -> void) -> string { /* mirror */ }

    public static fn captureBoth(action: fn() -> void) -> CaptureResult {
        // Returns struct { stdout: string, stderr: string }
    }
}

public struct CaptureResult {
    public stdout: string;
    public stderr: string;
}
```

### Decision 5: Bencher 类（`Bencher.z42`）

```z42
namespace z42.test;

public class Bencher {
    // Iterations are managed by the host runner; z42 side just provides the closure.
    // Returned to the runner as the body of [Benchmark] functions.
    public fn iter(closure: fn() -> void) -> void {
        // Marker: runner extracts this closure, calls it N times with timing.
        // Implementation: host-side; this z42 method just stores the closure.
        __bench_set_iter_closure(closure);
    }

    // Hide value from optimizer (criterion-style).
    public static fn blackBox<T>(value: T) -> T {
        return __bench_black_box(value);
    }
}
```

> Bencher 设计简化 v0.1：z42 端 `iter(closure)` 仅做 marker，实际重复执行与测时由 runner（R3）通过 `__bench_set_iter_closure` 拿到 closure 后控制。

### Decision 6: Native helpers (Rust 侧)

[src/runtime/src/corelib/test_io.rs](src/runtime/src/corelib/test_io.rs)：

```rust
use std::cell::RefCell;
use anyhow::Result;
use crate::metadata::types::Value;

thread_local! {
    static STDOUT_SINK: RefCell<Option<String>> = RefCell::new(None);
    static STDERR_SINK: RefCell<Option<String>> = RefCell::new(None);
}

pub fn install_stdout_sink(_args: &[Value]) -> Result<Value> {
    STDOUT_SINK.with(|sink| {
        *sink.borrow_mut() = Some(String::new());
    });
    Ok(Value::Null)
}

pub fn take_stdout_buffer(_args: &[Value]) -> Result<Value> {
    STDOUT_SINK.with(|sink| {
        let buf = sink.borrow_mut().take().unwrap_or_default();
        Ok(Value::String(buf.into()))
    })
}

// install / take for stderr mirror

// Called from corelib/io.rs::println
pub fn try_write_to_sink(s: &str) -> bool {
    STDOUT_SINK.with(|sink| {
        let mut g = sink.borrow_mut();
        if let Some(buf) = g.as_mut() {
            buf.push_str(s);
            true
        } else {
            false
        }
    })
}
```

[src/runtime/src/corelib/io.rs](src/runtime/src/corelib/io.rs) `println` 修改：

```rust
pub fn println(args: &[Value]) -> Result<Value> {
    let s = format_args(args)?;
    if !crate::corelib::test_io::try_write_to_sink(&format!("{s}\n")) {
        // Sink not installed → fall back to real stdout
        println!("{s}");
    }
    Ok(Value::Null)
}
```

[src/runtime/src/corelib/bench.rs](src/runtime/src/corelib/bench.rs)：

```rust
use std::time::Instant;
use std::hint::black_box;

pub fn now_ns(_args: &[Value]) -> Result<Value> {
    let dur = Instant::now().elapsed();
    Ok(Value::U64(dur.as_nanos() as u64))
}

pub fn black_box_value(args: &[Value]) -> Result<Value> {
    let v = args.get(0).cloned().unwrap_or(Value::Null);
    Ok(black_box(v))
}

// __bench_set_iter_closure: called by Bencher.iter; stores closure in thread-local
//   for runner pickup. Simpler alternative: runner monkey-patches benchmark
//   method dispatch to extract closure on first call. Implementation detail
//   for R3.
```

### Decision 7: Native dispatch 注册

[src/runtime/src/corelib/mod.rs](src/runtime/src/corelib/mod.rs) 把 4 个新 native 加入 dispatch 表：

```rust
pub fn dispatch_builtin(name: &str, args: &[Value]) -> Result<Value> {
    match name {
        // ... existing ...
        "__test_io_install_stdout_sink" => test_io::install_stdout_sink(args),
        "__test_io_take_stdout_buffer"  => test_io::take_stdout_buffer(args),
        "__test_io_install_stderr_sink" => test_io::install_stderr_sink(args),
        "__test_io_take_stderr_buffer"  => test_io::take_stderr_buffer(args),
        "__bench_now_ns"                => bench::now_ns(args),
        "__bench_black_box"             => bench::black_box_value(args),
        // ...
        _ => bail!("unknown builtin: {name}"),
    }
}
```

### Decision 8: 嵌套 capture 行为

stack-style：

```rust
thread_local! {
    static STDOUT_SINK_STACK: RefCell<Vec<String>> = RefCell::new(Vec::new());
}

pub fn install_stdout_sink(_: &[Value]) -> Result<Value> {
    STDOUT_SINK_STACK.with(|s| s.borrow_mut().push(String::new()));
    Ok(Value::Null)
}

pub fn take_stdout_buffer(_: &[Value]) -> Result<Value> {
    STDOUT_SINK_STACK.with(|s| {
        let buf = s.borrow_mut().pop().unwrap_or_default();
        Ok(Value::String(buf.into()))
    })
}

pub fn try_write_to_sink(s: &str) -> bool {
    STDOUT_SINK_STACK.with(|stack| {
        let mut g = stack.borrow_mut();
        // Innermost (top of stack) wins.
        if let Some(buf) = g.last_mut() {
            buf.push_str(s);
            true
        } else {
            false
        }
    })
}
```

嵌套语义：内层 install 把外层 sink "暂停"；inner take 后恢复 outer 接收。

## Implementation Notes

### attribute 系统调研

z42 当前是否完整支持自定义 `attribute X { fields }` 声明 + 应用到函数？需先看 [src/compiler/z42.Syntax/](src/compiler/z42.Syntax/)。

如果尚未支持，本 spec 拆为两步：
- R2.1: 实现 attribute 声明语法（如缺失）
- R2.2: 在 z42.test 库定义 attribute

调研结果记入 design.md 实施备忘。

### 泛型约束的妥协

z42 generics 在 L3 里。R2 实施时如果 `<T: Equatable>` 不可用：
- 暂时 fallback 到 `<T>` 无约束
- Assert.eq 实现内用 `==` 操作符（运行时 dispatch）
- L3 generics 落地后回归补约束

这是临时妥协，文档明确标注。

### `iterable<T>` 抽象

集合相关 Assert（contains / empty / lengthEq 等）需要 `iterable<T>` 抽象。z42 当前 LinkedList 实现了 `iter()`。为兼容多种集合：
- v0.1：`iterable<T>` 改为具体类型（如 `LinkedList<T>`）；多种集合各自重载
- v0.2：引入 `iterable<T>` trait 后统一

### TestIO 与 Console.println 的耦合

Console.println 必须先检查 sink。这把 z42.io 与 z42.test 耦合到 corelib 层（io.rs 调用 test_io::try_write_to_sink）。可以接受—— z42.io 的 println native 永远在 corelib，test sink 在 corelib，两者都是 host 内部实现细节。

## Testing Strategy

### z42.test 库自身测试（暂占位）

R2 完成后，z42.test/tests/ 仍然空（实际测试用例 R5 时补，含 dogfooding）。本期只验证库能编译为 .zpkg：
- `dotnet run --project src/compiler/z42.Driver -- build src/libraries/z42.test/z42.test.toml --release`
- 产出 `artifacts/libraries/z42.test/dist/z42.test.zpkg`

### Rust native 单测

- `test_io_tests.rs`:
  - install + take buffer 往返
  - try_write_to_sink 在无 sink 时返回 false
  - try_write_to_sink 在有 sink 时写入
  - 嵌套 install/take 栈式行为
- `bench_tests.rs`:
  - now_ns 单调递增
  - black_box 不消除值

### 集成测试（手动 .z42 demo）

写 `examples/test_lib_demo.z42`：

```z42
import z42.test.{Test, Assert, TestIO};
import z42.io.Console;

[Test]
fn test_assert_eq_passes() {
    Assert.eq(2 + 2, 4);
}

[Test]
fn test_capture_stdout() {
    let s = TestIO.captureStdout(|| {
        Console.WriteLine("hello");
    });
    Assert.eq(s, "hello\n");
}
```

编译 + （手动）调用 → 验证 Assert / TestIO 链路通。**runner 自动调度** 是 R3 的事，本期手动验证。

### 验证矩阵

| Scenario | 测试 |
|----------|------|
| Assert.eq 通过 | examples + 后续 R5 测试 |
| Assert.eq 失败抛 TestFailure | 同上 |
| Assert.throws 捕获正确异常 | 同上 |
| Assert.near 浮点精度 | 同上 |
| TestIO.captureStdout 单层 | examples |
| TestIO.captureStdout 嵌套 | Rust 单测 + examples |
| TestFailure 字段含 actual/expected/location | examples |
| Bencher.iter 接受 closure | 编译通过即可（runner 验证 R3） |
| z42.test 库编译为 .zpkg | build-stdlib.sh 输出 |
