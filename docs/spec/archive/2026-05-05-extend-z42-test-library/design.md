# Design: Extend z42.test Library — TestIO + Bencher + Assert ext

## Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│ z42 user test code                                                 │
│                                                                     │
│   var s = TestIO.captureStdout(() => {                              │
│       Console.WriteLine("hello");                                   │
│   });                                                               │
│   Assert.Throws<TestFailure>(() => Assert.Fail("boom"));           │
│   var b = new Bencher(); b.iter(() => work()); b.printSummary("w"); │
└────────────────────┬───────────────────────────────────────────────┘
                     │ closure captured by lifted function (impl-lambda-l2)
                     ▼
┌────────────────────────────────────────────────────────────────────┐
│ z42.test stdlib  (script-side, calls Action.Invoke())              │
│                                                                     │
│   TestIO.captureStdout(body) {                                      │
│       __test_io_install_stdout_sink();                              │
│       try { body.Invoke(); }                                        │
│       finally { return __test_io_take_stdout_buffer(); }            │
│   }                                                                 │
│                                                                     │
│   Bencher.iter(body) {                                              │
│       for warmup, body.Invoke();                                    │
│       for samples, t0=__bench_now_ns(); body.Invoke();              │
│         times[i] = __bench_now_ns() - t0;                           │
│       compute Min/Max/Median/Total                                  │
│   }                                                                 │
│                                                                     │
│   Assert.Throws<E>(body) {                                          │
│       try { body.Invoke(); }                                        │
│       catch (Exception e) { check `e is E` else throw TestFailure } │
│       throw TestFailure("did not throw")                            │
│   }                                                                 │
└────────────────────┬───────────────────────────────────────────────┘
                     │ [Native("__name")] dispatch
                     ▼
┌────────────────────────────────────────────────────────────────────┐
│ Rust corelib                                                       │
│                                                                     │
│   thread_local STDOUT_SINKS: RefCell<Vec<Vec<u8>>>                  │
│   thread_local STDERR_SINKS: RefCell<Vec<Vec<u8>>>                  │
│                                                                     │
│   builtin_println(text):                                            │
│       if let Some(top) = STDOUT_SINKS.borrow_mut().last_mut() {     │
│           top.extend_from_slice(text.as_bytes());                   │
│           top.push(b'\n');                                          │
│       } else { println!("{text}"); }                                │
│                                                                     │
│   __test_io_install_stdout_sink: STDOUT_SINKS.push(Vec::new())      │
│   __test_io_take_stdout_buffer:                                     │
│       String::from_utf8_lossy(STDOUT_SINKS.pop().unwrap()).into()   │
│                                                                     │
│   __bench_now_ns: Instant::now() → ns since process_start_anchor    │
│   __bench_black_box: identity (no-op)                               │
└────────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: stdout/stderr sink 用 stack 而非 single slot

**问题**：`captureStdout` 嵌套调用时，内层 capture 的输出该归谁？

**选项**：
- A — single slot：嵌套时报错或覆盖外层
- B — stack：内层 push、内层 take pop；外层只看到外层期间的输出

**决定**：选 **B (stack)**。

**理由**：
- 嵌套是合法用例：测试方法 A 内部调用 helper B，A 用 captureStdout 捕获 B 的输出；A 不应假设 B 内部不再 capture
- Rust 端 thread_local Vec<Vec<u8>> 实现成本与 single slot 几乎相同
- stack 行为可被简单的"finally pop"模式保证（spec scenario 4）；A 的 single-slot 在嵌套异常时的状态恢复反而更复杂
- spec 锁定为"内层先卸载"（push/pop）

### Decision 2: 异常透传 + 必 finally pop

**问题**：`captureStdout(body)` 中 `body` 抛异常时，sink 怎么清理？

**选项**：
- A — Rust 端把"卸载 sink"放进 install 的 RAII guard，z42 端 try/finally 不需要
- B — z42 端 `try { body.Invoke(); } finally { take_buffer(); }` 显式卸载

**决定**：选 **B (z42 端 try/finally)**。

**理由**：
- z42 已支持 try/finally；正面写出来语义最清晰，不用埋 Rust-side 不可见的 RAII
- `try/finally` 的 finally 必跑，与 spec scenario 4 直接对应
- Rust 端 install/take 是纯 push/pop 数据结构操作，没必要做 RAII guard wrapping
- 缺点：z42 端必须正确写 finally；通过实现侧统一封装到 `TestIO.captureStdout` 内部，用户的 user code 不会接触

### Decision 3: Bencher.iter 接 Action 而非 Func<R>

**问题**：iter body 是否允许返回值？

**选项**：
- A — 仅 `Action`（无返回）
- B — 仅 `Func<R>`（有返回）
- C — 重载两个

**决定**：选 **A (Action)**。

**理由**：
- criterion / cargo-bench / google-benchmark 业界一致：iter body void return + 用 closure capture 外部变量传递结果
- Func<R> 让 API 表面变复杂（iter 自己要不要存 R？）
- 如果未来需要 Func 重载随时可加（无破坏性变更）

### Decision 4: blackBox 接 object 而非泛型 <T>（2026-05-05 实施期间修订）

**问题**：interp 没有 dead-code elimination，blackBox 现在没必要做事；但 API 形态决定了用户代码迁到 JIT 时是否要重写。

**取舍**：

- ❌ **泛型 <T>(T)**：parser 在表达式上下文不识别方法级显式 generic call（与 `<` 冲突）；generic extern 又不能从参数推断 T。两条路都走不通。
- ✅ **`object` 形态**：自动装箱所有类型；运行时 identity；接口稳定。

```z42
public static class BenchHelpers {
    [Native("__bench_black_box")]
    public static extern object blackBox(object value);
}
```

**用法**：

```z42
b.iter(() => Math.Sqrt(BenchHelpers.blackBox(2.0)));
```

`object` 返回拆箱依赖 z42 隐式转换；当前用例（数值/字符串）测试通过。等 z42 parser 支持 `Foo.Bar<T>(args)` 显式 call-site 或 extern 推断时升级回泛型。

### Decision 5: Bencher 默认 warmup=10 / samples=100

**问题**：默认值多大？

**调研依据**：
- criterion (Rust)：warm_up_time=3s + measurement_time=5s（动态决定 sample 数）
- google-benchmark：min_time=1s（动态）
- pytest-benchmark：rounds=5, iterations 由 calibration 决定

**决定**：固定数 10 / 100。

**理由**：
- 不引入 calibration（要测当前 body 一次拿到耗时再决定 sample 数）—— 简化
- 100 samples 给中位数 / 范围统计提供合理的样本数
- 用户嫌慢可以 `new Bencher(2, 5)` 调小；嫌噪声大可以调大
- 这不是 production benchmarking 框架；是"快速看一段代码大概多快"

### Decision 6: 单调时钟实现

**实现**：每次 `__bench_now_ns` 调用 `std::time::Instant::now().duration_since(EPOCH).as_nanos()`，其中 `EPOCH = OnceLock<Instant>`（首次调用时 capture）。

**为什么不直接 `Instant.elapsed_since`**：FFI 边界要的是 `i64`/`u64` 标量，Instant 不可序列化；EPOCH-based ns 是 z42 用户代码可以做减法的形态。

**精度警告**：纳秒数从首次调用算起；测量两次调用的 delta 是单调的，但绝对值无意义（不是 wall clock）。spec 已澄清。

### Decision 7: Throws 不带类型断言（2026-05-05 实施期间二次修订）

**问题**：z42 当前所有"runtime 拿到 catch 的 Exception 子类"路径都坏：

1. **generic <E>**：[IsInstance](src/runtime/src/interp/exec_instr.rs#L535) IR 接受编译期固定 class_name，generic 参数不参与 substitution；`typeof(E)` 在 [ExprParser.Atoms.cs:77-90](src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs#L77) 是 parse-time desugar 成字面字符串 `"E"`。
2. **`e is X`（X 为短名）**：当 `e` 静态类型是 `Exception`、运行时类型是 `Std.TestFailure` 时，IsInstance 返回 false。实测确认（dogfood debug 期间），猜测原因是 IR-side `class_name` 用短名而 vtable / type_registry 用 FQ 名 → mismatch。
3. **`e.GetType()`**：VCall 在 Exception 子类的 vtable 上找不到 Object 的 GetType——继承链跨多层时方法表未完整传递。

**取舍方案**（按强弱排序）：
- ❌ generic `<E>`：阻塞于 1+2
- ❌ `Predicate<Exception>`：等价转嫁给用户写 `e is X`，仍阻塞于 2
- ❌ `string typeName` + GetType 比对：阻塞于 3
- ✅ **不带类型断言**：`Throws(Action)` 仅保证"是否抛出"，类型断言改为依赖 `[ShouldThrow<E>]`（编译期 chain 写入 TIDX，runner 端字符串匹配，绕开所有 reflection）

**决定**：选最后这个（弱契约，但当前语言能力下唯一可靠实现）。

```z42
public static void Throws(Action body) {
    try { body(); }
    catch (Exception e) { return; }   // 任意异常都算命中
    throw new TestFailure("expected to throw but no exception was thrown");
}

public static void DoesNotThrow(Action body) {
    try { body(); }
    catch (Exception e) {
        throw new TestFailure($"expected no exception but got: {e.Message}");
    }
}
```

**用户视角分层**：

| 需求 | API |
|---|---|
| 整个 [Test] 应抛特定类型 | `[ShouldThrow<TestFailure>]` 注解（类型安全） |
| 这一段代码应抛任何异常 | `Assert.Throws(() => ...)` |
| 这一段代码不应抛 | `Assert.DoesNotThrow(() => ...)` |
| 自定义类型断言 | 用户自己写 `try / catch (TestFailure e) { ... }`（catch 子句的静态类型不受 z42 reflection bug 影响） |

类型敏感的 method-level Throws<E> 等三个 reflection bug 任一修好后单独 spec 升级。

### Decision 8: dogfood 测试要不要嵌套 captureStdout？

**问题**：spec scenario 5 的"嵌套 capture"需要测试。

**决定**：dogfood.z42 加 1 个嵌套测试。是 z42.test 自检的 stress case，价值高。

## Implementation Notes

### Rust corelib sink (test-side hooks for capture)

`src/runtime/src/corelib/io.rs`：

```rust
use std::cell::RefCell;

thread_local! {
    static STDOUT_SINKS: RefCell<Vec<Vec<u8>>> = RefCell::new(Vec::new());
    static STDERR_SINKS: RefCell<Vec<Vec<u8>>> = RefCell::new(Vec::new());
}

pub fn builtin_println(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let text = args.first().map(value_to_str).unwrap_or_default();
    STDOUT_SINKS.with(|sinks| {
        let mut s = sinks.borrow_mut();
        if let Some(top) = s.last_mut() {
            top.extend_from_slice(text.as_bytes());
            top.push(b'\n');
        } else {
            println!("{}", text);
        }
    });
    Ok(Value::Null)
}

pub fn builtin_test_io_install_stdout_sink(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    STDOUT_SINKS.with(|s| s.borrow_mut().push(Vec::new()));
    Ok(Value::Null)
}

pub fn builtin_test_io_take_stdout_buffer(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let bytes = STDOUT_SINKS.with(|s| s.borrow_mut().pop().unwrap_or_default());
    Ok(Value::Str(String::from_utf8_lossy(&bytes).into_owned()))
}
```

stderr 镜像。

### Bench native helpers

`src/runtime/src/corelib/bench.rs`：

```rust
use std::sync::OnceLock;
use std::time::Instant;

static EPOCH: OnceLock<Instant> = OnceLock::new();

pub fn builtin_bench_now_ns(_ctx: &VmContext, _: &[Value]) -> Result<Value> {
    let epoch = EPOCH.get_or_init(Instant::now);
    let ns = Instant::now().duration_since(*epoch).as_nanos() as i64;
    Ok(Value::I64(ns))
}

pub fn builtin_bench_black_box(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(args.first().cloned().unwrap_or(Value::Null))
}
```

### z42-side TestIO

```z42
namespace Std.Test;

using Std;
using Std.IO;

public class CaptureResult {
    public string Stdout { get; }
    public string Stderr { get; }
    public CaptureResult(string stdout, string stderr) {
        Stdout = stdout;
        Stderr = stderr;
    }
}

public static class TestIO {
    public static string captureStdout(Action body) {
        __install_stdout_sink();
        try {
            body.Invoke();
        } finally {
            // 不论 body 是否抛异常，都要 pop sink；buffer 在 take 时返回
        }
        return __take_stdout_buffer();
    }
    // captureStderr / captureBoth 类似
}

[Native("__test_io_install_stdout_sink")]
internal static extern void __install_stdout_sink();

[Native("__test_io_take_stdout_buffer")]
internal static extern string __take_stdout_buffer();
```

注意 Throws<E> 的 try/finally 模式：finally **必须** take buffer 才能正确 pop sink，否则下次 capture 看到上次没 pop 的 sink。代码组织成 take-in-finally：

```z42
public static string captureStdout(Action body) {
    __install_stdout_sink();
    try {
        body.Invoke();
        return __take_stdout_buffer();
    } catch (Exception e) {
        // 异常路径也要 pop（take 即 pop），然后重抛
        __take_stdout_buffer();   // discard
        throw;
    }
}
```

**或者**用 finally + outer var：

```z42
public static string captureStdout(Action body) {
    __install_stdout_sink();
    string result = "";
    try {
        body.Invoke();
        result = __take_stdout_buffer();
    } finally {
        // 如果 try 块没成功 take（抛异常前），现在 take 一次 pop
        if (result == "") {
            __take_stdout_buffer();   // discard buffer; sink popped
        }
    }
    return result;
}
```

第一个版本更清晰；用第一个。

### z42-side Bencher

```z42
namespace Std.Test;

using Std;

public class Bencher {
    public int WarmupIters { get; }
    public int Samples { get; }
    public long MinNs { get; private set; }
    public long MaxNs { get; private set; }
    public long MedianNs { get; private set; }
    public long TotalNs { get; private set; }

    public Bencher() : this(10, 100) {}

    public Bencher(int warmupIters, int sampleIters) {
        WarmupIters = warmupIters;
        Samples = sampleIters;
    }

    public void iter(Action body) {
        // warmup
        int w = 0;
        while (w < WarmupIters) { body.Invoke(); w = w + 1; }
        // measure
        var times = new List<long>();
        int i = 0;
        while (i < Samples) {
            long t0 = __bench_now_ns();
            body.Invoke();
            long t1 = __bench_now_ns();
            times.Add(t1 - t0);
            i = i + 1;
        }
        // sort + stats（List 暂无内置 sort；用简单选择排序或冒泡）
        sortInPlace(times);
        MinNs    = times[0];
        MaxNs    = times[Samples - 1];
        MedianNs = times[Samples / 2];
        long total = 0;
        int j = 0;
        while (j < Samples) { total = total + times[j]; j = j + 1; }
        TotalNs = total;
    }

    public void printSummary(string label) {
        Console.WriteLine($"bench[{label}] min={MinNs}ns median={MedianNs}ns max={MaxNs}ns samples={Samples}");
    }

    private static void sortInPlace(List<long> xs) {
        int n = xs.Count;
        int i = 1;
        while (i < n) {
            long key = xs[i];
            int j = i - 1;
            while (j >= 0 && xs[j] > key) {
                xs[j + 1] = xs[j];
                j = j - 1;
            }
            xs[j + 1] = key;
            i = i + 1;
        }
    }
}

public static class BenchHelpers {
    [Native("__bench_black_box")]
    public static extern T blackBox<T>(T value);
}

[Native("__bench_now_ns")]
internal static extern long __bench_now_ns();
```

### TestAttributeValidator E0912 完整化

`ValidateBenchmarkPartialSignature` → `ValidateBenchmarkFullSignature`：参 `Std.Test.Bencher` symbol（在 SemanticModel.Classes 找）。

```csharp
private static void ValidateBenchmarkFullSignature(
    FunctionDecl fn, SemanticModel sem, DiagnosticBag diags)
{
    // R4.A 已校验 void return + non-generic
    // R2.C 加：exactly 1 param, type is Bencher
    if (fn.Params.Count != 1) {
        diags.Add(...);
        return;
    }
    var p = fn.Params[0];
    var paramTypeName = ExtractTypeName(p.Type);
    if (paramTypeName != "Bencher") {
        diags.Add(...);
    }
}
```

调用点替换 ValidateBenchmarkPartialSignature。

## Testing Strategy

### Rust 单元测试

- `corelib/io_tests.rs`：sink stack push/pop、嵌套行为、未安装时直接 println
- `corelib/bench_tests.rs`：`__bench_now_ns` 单调正、`__bench_black_box` identity

### C# 单元测试

- `TestAttributeTests.cs` 加 4 个 E0912 case（参 spec scenarios）

### z42-side 端到端（dogfood.z42）

- 5 个 TestIO scenario（来自 spec）
- 4 个 Assert ext scenario
- 4 个 Bencher scenario
- 总计 ~13 个新 [Test]，dogfood 期望 8 → 21（含旧 8）

### 验证命令

```bash
just test                                              # dotnet + test-vm + cross-zpkg 全绿
cargo test --manifest-path src/runtime/Cargo.toml      # corelib 新单测
just test-stdlib z42.test                              # dogfood 21/0
./scripts/test-stdlib.sh                               # 6 lib 全绿
```
