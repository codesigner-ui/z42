# Spec: z42.test Library — TestIO + Bencher + Assert ext

> 2026-05-05 重写。原 R2 minimal scenarios（Assert 9 方法 / TestFailure / SkipSignal）已经验证，本文件仅记录新增能力的可验证场景。

## ADDED Requirements

### Requirement: TestIO captures stdout / stderr

`Std.Test.TestIO` 三个静态方法捕获被测代码的 console 输出。

#### Scenario: captureStdout 收集 println 输出
- **GIVEN** `var s = TestIO.captureStdout(() => { Console.WriteLine("hello"); Console.WriteLine("world"); });`
- **THEN** `s` 等于 `"hello\nworld\n"`
- **AND** captureStdout 调用结束后再 `Console.WriteLine("after")` → 实际写到进程 stdout（sink 已卸载）

#### Scenario: captureStderr 不捕获 stdout
- **GIVEN** `var s = TestIO.captureStderr(() => { Console.WriteLine("on stdout"); ConsoleError.WriteLine("on stderr"); });`
- **THEN** `s` 等于 `"on stderr\n"`
- **AND** `"on stdout"` 仍写到进程 stdout（不被 sink 拦截）

#### Scenario: captureBoth 同时捕获两路
- **GIVEN** `var r = TestIO.captureBoth(() => { Console.WriteLine("a"); ConsoleError.WriteLine("b"); });`
- **THEN** `r.Stdout` 等于 `"a\n"`，`r.Stderr` 等于 `"b\n"`

#### Scenario: capture body 抛异常时 sink 仍被卸载
- **GIVEN** `try { TestIO.captureStdout(() => throw new Exception("boom")); } catch { }`
- **THEN** 后续 `Console.WriteLine("normal")` 写到进程 stdout（不再走 buffer）
- **AND** 抛出的 Exception 透传到 captureStdout 调用方

#### Scenario: 嵌套 capture 内层先卸载
- **GIVEN** `var outer = TestIO.captureStdout(() => { Console.WriteLine("outer-pre"); var inner = TestIO.captureStdout(() => Console.WriteLine("inner")); Console.WriteLine($"outer-post:{inner.Trim()}"); });`
- **THEN** `outer` 等于 `"outer-pre\nouter-post:inner\n"`
- **AND** `inner` 等于 `"inner\n"`

> 实现要点：sink 用 stack（thread-local Vec），install push、take pop。

### Requirement: Bencher measures elapsed nanoseconds

`Std.Test.Bencher` 为代码段提供单调时钟测量。

#### Scenario: 默认 ctor 跑 110 次
- **GIVEN** `var b = new Bencher(); int n = 0; b.iter(() => n = n + 1);`
- **THEN** `n` 等于 `110`（warmup 10 + samples 100；默认值）
- **AND** `b.Samples` 等于 `100`

#### Scenario: 自定义 ctor
- **GIVEN** `var b = new Bencher(2, 5); int n = 0; b.iter(() => n = n + 1);`
- **THEN** `n` 等于 `7`
- **AND** `b.Samples` 等于 `5`

#### Scenario: MedianNs 单调正
- **GIVEN** body 至少做 1 个 `Math.Sqrt` 调用确保非零耗时
- **THEN** `b.MinNs >= 0` 且 `b.MaxNs >= b.MinNs` 且 `b.MedianNs` 在 `[MinNs, MaxNs]` 闭区间
- **AND** `b.TotalNs` >= `b.Samples * b.MinNs`

#### Scenario: blackBox 透传值不被消除
- **GIVEN** `int x = BenchHelpers.blackBox<int>(42);`
- **THEN** `x == 42`（interp 路径 no-op；JIT 端预留 hook 防 dead-code 消除，但行为契约相同）

#### Scenario: printSummary 输出包含 label + 三个统计量
- **GIVEN** `var b = new Bencher(2, 3); b.iter(() => {}); var s = TestIO.captureStdout(() => b.printSummary("foo"));`
- **THEN** `s` 包含子串 `"foo"`
- **AND** `s` 包含 `"min"`、`"median"`、`"max"`（任一格式都算命中——具体格式不在 spec 内锁定）

### Requirement: Assert.Throws<E> / DoesNotThrow / EqualApprox

> **API 修订（2026-05-05 实施期间）**：z42 三处反射机制（generic-E
> IsInstance / `e is X` cross-module / `e.GetType().__name` on Exception
> 子类）当前都无法可靠识别 catch 入参子类型；详见 design.md Decision 7。
> `Throws` 简化为"任意 throw 命中"形态。类型敏感的"该测试应抛特定类型"
> 用 `[ShouldThrow<E>]` 测试级 attribute（编译期 chain，runner 字符串匹配，
> 不依赖任何反射）。

#### Scenario: Throws 命中（任意 throw）
- **GIVEN** `Assert.Throws(() => Assert.Fail("boom"));`
- **THEN** 不抛出（命中预期）

#### Scenario: Throws 接受 SkipSignal / 普通 Exception
- **GIVEN** `Assert.Throws(() => Assert.Skip("any"));` 与 `Assert.Throws(() => throw new Exception("plain"));`
- **THEN** 两者都不抛出（任意 throw 都算命中）

#### Scenario: Throws 未抛
- **GIVEN** `Assert.Throws(() => { });`
- **THEN** 抛 `TestFailure`，message 类似 `"expected to throw but no exception was thrown"`

#### Scenario: DoesNotThrow 通过路径
- **GIVEN** `Assert.DoesNotThrow(() => 1 + 1);`
- **THEN** 不抛出

#### Scenario: DoesNotThrow 失败
- **GIVEN** `Assert.DoesNotThrow(() => throw new Exception("oops"));`
- **THEN** 抛 `TestFailure`，message 包含原始异常类型 + message

#### Scenario: EqualApprox 容差内通过
- **GIVEN** `Assert.EqualApprox(1.0001, 1.0, 0.001);`
- **THEN** 不抛出

#### Scenario: EqualApprox 超容差失败
- **GIVEN** `Assert.EqualApprox(1.5, 1.0, 0.1);`
- **THEN** 抛 `TestFailure`，actual / expected / eps 都体现在 message

### Requirement: TestAttributeValidator E0912 first-param Bencher check

R4.A 留给"Bencher type 不存在"的延后校验现在补齐。

#### Scenario: [Benchmark] void f(Bencher b) → 通过
- **GIVEN** 函数 `[Benchmark] void f(Bencher b) { }`
- **THEN** TestAttributeValidator 不报错

#### Scenario: [Benchmark] void f() → E0912
- **GIVEN** 函数 `[Benchmark] void f() { }`
- **THEN** 报 `E0912`，message 类似 `[Benchmark] function 'f' must have first parameter of type Bencher`

#### Scenario: [Benchmark] void f(int x) → E0912
- **GIVEN** 函数 `[Benchmark] void f(int x) { }`
- **THEN** 报 `E0912`，message 类似 `[Benchmark] first parameter must be Bencher, got int`

#### Scenario: [Benchmark] void f(Bencher b, int extra) → E0912 forbid extras
- **GIVEN** 函数 `[Benchmark] void f(Bencher b, int x) { }`
- **THEN** 报 `E0912`，message 类似 `[Benchmark] must take exactly one parameter (the Bencher); got 2`

> 注：当前 runner 不跑 [Benchmark]，但编译期校验要先到位，后续 runner mode 才能依赖它。

## MODIFIED Requirements

### Requirement: Console.WriteLine respects active stdout sink

**Before:** `builtin_println` 无条件写 process stdout。

**After:** thread-local stdout sink stack 非空 → 写到 stack top buffer；空 → 走 process stdout（行为保持兼容）。stderr 同理。

### Requirement: TestAttributeValidator E0912 [Benchmark] signature complete

**Before:** R4.A 仅校验 `[Benchmark]` 返回 void + 非 generic。注释明确"first-parameter-is-Bencher check pending R2.C"。

**After:** 校验完整化：第一参数必须是 `Bencher`；exactly 1 个参数（不能 0 也不能 2+）。

## IR Mapping

无新 IR 指令。新 native builtins 通过现有 `[Native("__name")]` + `dispatch_table` 路径暴露：

| Native key | 用途 |
|---|---|
| `__test_io_install_stdout_sink` | TestIO.captureStdout 实现 |
| `__test_io_take_stdout_buffer` | 同上，take 后卸载 sink |
| `__test_io_install_stderr_sink` | TestIO.captureStderr 实现 |
| `__test_io_take_stderr_buffer` | 同上 |
| `__bench_now_ns` | Bencher 时钟 |
| `__bench_black_box` | blackBox 透传 |

## Pipeline Steps

- [x] Lexer / Parser / TypeChecker — 无改动（lambda + delegate 已落）
- [x] IrGen — 无改动
- [ ] **TestAttributeValidator** — E0912 first-param-is-Bencher 校验
- [ ] **VM corelib** — 6 个新 native + sink dispatch
- [x] zbc / zpkg 格式 — 无改动
