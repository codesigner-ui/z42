# Spec: z42.test Library API

## ADDED Requirements

### Requirement: Attribute 类型暴露

#### Scenario: 用户可声明 [Test]

- **WHEN** 用户写 `[Test] fn my_test() { ... }`
- **WHEN** 编译器加载 z42.test 库
- **THEN** 编译通过；TestIndex section 含该函数 entry (kind=Test)

#### Scenario: 用户可声明 [Skip(reason)]

- **WHEN** 用户写 `[Test] [Skip(reason: "blocked")] fn x() { ... }`
- **THEN** 编译通过；TestEntry.flags 含 Skipped；skip_reason_str_idx 指向 "blocked"

#### Scenario: 8 个 attribute 全部可用

- **WHEN** 用户写包含 8 种 attribute 的 .z42 程序
- **THEN** 全部编译通过；产物 zbc TestIndex section 含正确 entry

---

### Requirement: TestFailure 异常类

#### Scenario: 含全部字段

- **WHEN** 阅读 `z42.test.TestFailure`
- **THEN** 含字段：actual / expected / location / message
- **AND** 是 Exception 子类

#### Scenario: 默认参数 ctor

- **WHEN** 调用 `TestFailure(message: "x")`
- **THEN** actual / expected / location 默认空字符串

---

### Requirement: Assert 相等

#### Scenario: Assert.eq 相等通过

- **WHEN** `Assert.eq(2 + 2, 4)`
- **THEN** 不抛异常

#### Scenario: Assert.eq 不等抛 TestFailure

- **WHEN** `Assert.eq(1, 2)`
- **THEN** 抛 TestFailure
- **AND** failure.actual = "1"，failure.expected = "2"

#### Scenario: Assert.notEq 不等通过

- **WHEN** `Assert.notEq(1, 2)`
- **THEN** 不抛

---

### Requirement: Assert 布尔

#### Scenario: isTrue / isFalse

- **WHEN** `Assert.isTrue(true)` / `Assert.isFalse(false)`
- **THEN** 不抛
- **AND** `Assert.isTrue(false)` 抛 TestFailure

#### Scenario: isNull / isNotNull

- **WHEN** `Assert.isNull(null)` 或 `Assert.isNotNull(obj)`
- **THEN** 不抛

---

### Requirement: Assert 浮点

#### Scenario: near 在 epsilon 内通过

- **WHEN** `Assert.near(1.0, 1.0 + 1.0e-12)` (默认 epsilon=1e-9)
- **THEN** 不抛

#### Scenario: near 超 epsilon 抛

- **WHEN** `Assert.near(1.0, 1.5, 0.01)`
- **THEN** 抛 TestFailure；failure.expected 含 "± 0.01"

#### Scenario: relativeNear

- **WHEN** `Assert.relativeNear(1000.0, 1001.0, 0.01)` (1% 相对误差)
- **THEN** 不抛

---

### Requirement: Assert 异常

#### Scenario: throws 捕获到正确类型

- **WHEN** `let e = Assert.throws<DivByZero>(|| { 1 / 0 })`
- **THEN** 返回的 e 是 DivByZero 实例

#### Scenario: throws 没抛异常时失败

- **WHEN** `Assert.throws<DivByZero>(|| { 1 + 1 })`
- **THEN** 抛 TestFailure；message 含 "expected exception was not thrown"

#### Scenario: throws 抛错类型时失败

- **WHEN** `Assert.throws<DivByZero>(|| { throw OtherException(...) })`
- **THEN** 抛 TestFailure；failure.actual 含 "OtherException"

---

### Requirement: Assert 主动控制

#### Scenario: fail 立即抛

- **WHEN** `Assert.fail("not implemented")`
- **THEN** 抛 TestFailure，message="not implemented"

#### Scenario: skip 抛 SkipSignal

- **WHEN** `Assert.skip("env missing")`
- **THEN** 抛 SkipSignal；reason="env missing"
- **AND** runner 应识别 SkipSignal 标记测试为 skipped（runner 行为 → R3）

---

### Requirement: TestIO 捕获

#### Scenario: captureStdout 捕获单次输出

- **WHEN** `let s = TestIO.captureStdout(|| { Console.WriteLine("a"); })`
- **THEN** `s == "a\n"`

#### Scenario: captureStdout 捕获多次输出

- **WHEN** `TestIO.captureStdout(|| { Console.WriteLine("a"); Console.WriteLine("b"); })`
- **THEN** 返回 `"a\nb\n"`

#### Scenario: captureStdout 嵌套（栈式）

- **WHEN** `TestIO.captureStdout(|| { Console.WriteLine("outer"); TestIO.captureStdout(|| { Console.WriteLine("inner"); }); Console.WriteLine("outer-resume"); })`
- **THEN** 外层捕获 = "outer\nouter-resume\n"
- **AND** 内层捕获 = "inner\n"

#### Scenario: capture 内异常时缓冲不泄露

- **WHEN** action 抛异常
- **THEN** sink 已 take（不留在 thread-local）

#### Scenario: 无 capture 时 println 走真实 stdout

- **WHEN** `Console.WriteLine("x")` 在 capture 之外
- **THEN** 写到进程 stdout

---

### Requirement: Bencher

#### Scenario: Bencher.iter 接受 closure

- **WHEN** 用户写 `[Benchmark] fn bench_x(b: Bencher) { b.iter(|| { /* work */ }); }`
- **THEN** 编译通过（runner 后续读 closure 调度，R3 范畴）

#### Scenario: blackBox 不消除值

- **WHEN** 在 release 编译产物中 `Bencher.blackBox(x)`
- **THEN** LLVM 不优化掉 x（验证 z42 / Rust 端 black_box 工作）

---

### Requirement: Native helpers (Rust)

#### Scenario: __test_io_install_stdout_sink 安装栈帧

- **WHEN** 调用 install
- **THEN** 后续 println 写 buffer 而非 stdout
- **AND** 栈深度增加 1

#### Scenario: __test_io_take_stdout_buffer 取出 + 弹栈

- **WHEN** 已 install 后调用 take
- **THEN** 返回累积内容字符串
- **AND** 栈深度减少 1
- **AND** 恢复上层 sink（若有）或写真实 stdout（若栈空）

#### Scenario: __bench_now_ns 单调递增

- **WHEN** 连续两次 `__bench_now_ns()`
- **THEN** 第二次 ≥ 第一次

#### Scenario: __bench_black_box 透传

- **WHEN** `__bench_black_box(42)`
- **THEN** 返回值 == 42（不变化）
- **AND** LLVM 不消除该调用

---

### Requirement: 库构建

#### Scenario: 编译为 .zpkg

- **WHEN** 执行 `./scripts/build-stdlib.sh`
- **THEN** `artifacts/libraries/z42.test/dist/z42.test.zpkg` 存在
- **AND** 含 Test / TestCase / Benchmark / TestIO / Bencher / Assert / TestFailure 等 public type 元数据

#### Scenario: 其他 stdlib 不引入循环依赖

- **WHEN** 检查 z42.test 的依赖图
- **THEN** 仅依赖 z42.core；不依赖 z42.collections / z42.io / z42.text 等（除 native interop 通过 corelib）

---

### Requirement: 文档

#### Scenario: testing.md 含 API 详解

- **WHEN** 阅读 [docs/design/testing.md](docs/design/testing.md)
- **THEN** 含 z42.test API 全集表（Assert / TestIO / Bencher / Failure）

#### Scenario: library-z42-test.md 内部机制

- **WHEN** 阅读 [docs/design/library-z42-test.md](docs/design/library-z42-test.md)
- **THEN** 含 4 个 native helper 描述 + thread-local sink 栈式语义图
