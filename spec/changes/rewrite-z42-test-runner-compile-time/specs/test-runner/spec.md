# Spec: z42-test-runner (Compile-Time Discovery)

## ADDED Requirements

### Requirement: Discovery via TestIndex section

#### Scenario: 加载 .zbc 时直接读 test_index

- **WHEN** runner 启动 + 给定一个含 TestEntry 的 .zbc
- **THEN** 不扫 method table；直接从 LoadedArtifact.test_index 拿到 entry 列表

#### Scenario: 不含 [Test] 的 .zbc 无错过

- **WHEN** runner 给定一个不含任何 z42.test attribute 的 .zbc
- **THEN** 退出码 3（无测试可跑），输出"no tests found"

---

### Requirement: Test 执行

#### Scenario: 通过的测试

- **WHEN** [Test] 函数正常返回
- **THEN** TestResult.status = passed
- **AND** 记录 duration

#### Scenario: assertion 失败

- **WHEN** [Test] 内 `Assert.eq(1, 2)`
- **THEN** runner catch z42.test.TestFailure
- **AND** TestResult.status = failed
- **AND** failure.actual = "1"，failure.expected = "2"

#### Scenario: 抛任意异常

- **WHEN** [Test] 内代码抛非 TestFailure 异常
- **THEN** TestResult.status = failed
- **AND** failure.message 含异常类型与原始 message

#### Scenario: ShouldThrow 验证通过

- **WHEN** 函数标 `[Test] [ShouldThrow<DivByZero>]` 且抛了 DivByZero
- **THEN** TestResult.status = passed

#### Scenario: ShouldThrow 但没抛

- **WHEN** 函数标 `[ShouldThrow<E>]` 但正常返回
- **THEN** TestResult.status = failed，message = "expected exception not thrown"

#### Scenario: ShouldThrow 抛错类型

- **WHEN** 函数标 `[ShouldThrow<DivByZero>]` 但抛 OtherException
- **THEN** TestResult.status = failed，含期望/实际类型对比

---

### Requirement: Setup / Teardown

#### Scenario: Setup 在每个 [Test] 前调用

- **WHEN** module 含 `[Setup] fn s()` 与 `[Test] fn t()`
- **THEN** runner 跑 t 之前先调 s
- **AND** 多个 [Setup] 按定义顺序

#### Scenario: Teardown 在每个 [Test] 后调用（含失败）

- **WHEN** [Test] 失败
- **THEN** [Teardown] 仍被调用
- **AND** 不影响 final status

#### Scenario: Setup 失败时跳过 [Test]

- **WHEN** [Setup] 抛异常
- **THEN** [Test] 不跑，status = failed，message = "setup failed: ..."
- **AND** [Teardown] 仍调用

---

### Requirement: Skip / Ignore

#### Scenario: Skip(reason) 标记

- **WHEN** 函数 `[Test][Skip(reason: "blocked")]`
- **THEN** TestResult.status = skipped，reason = "blocked"
- **AND** 测试体不执行

#### Scenario: 运行时 Assert.skip 抛 SkipSignal

- **WHEN** [Test] 体内 `Assert.skip("env missing")`
- **THEN** runner 识别 SkipSignal → status = skipped，reason = "env missing"

#### Scenario: Ignore

- **WHEN** 函数标 `[Ignore]`
- **THEN** 不出现在测试列表
- **AND** total / passed / failed 计数不含

---

### Requirement: TestCase 参数化

#### Scenario: 多个 TestCase 各自一个 entry

- **WHEN** 函数 `[Test][TestCase(0,0)][TestCase(10,55)] fn fib_test(n: i32, exp: i32)`
- **THEN** 该函数被跑 2 次，参数分别为 (0,0) 和 (10,55)
- **AND** 每个 case 独立 TestResult，名字带参数信息（如 "fib_test(n=10, exp=55)"）

#### Scenario: 任一 case 失败标记整体失败

- **WHEN** `[TestCase(1)][TestCase(2)]` 第一个 case 失败、第二个通过
- **THEN** 整体 status = failed
- **AND** 输出列出哪个 case 失败

---

### Requirement: stdout 默认捕获（参 Rust libtest）

#### Scenario: 通过测试时丢弃 stdout

- **WHEN** [Test] 内调用 `Console.WriteLine("debug")` 且测试通过
- **THEN** runner 不打印 "debug" 到屏幕
- **AND** 输出只显示测试名与通过状态

#### Scenario: 失败测试时显示 stdout

- **WHEN** [Test] 内调用 `Console.WriteLine("debug")` 后失败
- **THEN** runner 输出含 "debug" 行（捕获显示）

#### Scenario: --show-output 强制显示

- **WHEN** runner 用 `--show-output` 选项
- **THEN** 通过测试也打印捕获的 stdout

---

### Requirement: CLI 接口（继承 P2）

#### Scenario: --filter 正则过滤

- **WHEN** `z42-test-runner . --filter '^test_string'`
- **THEN** 只跑匹配的测试

#### Scenario: --format json

- **WHEN** `--format json`
- **THEN** 输出符合原 P2 spec design.md Decision 6 的 JSON

#### Scenario: --format tap

- **WHEN** `--format tap`
- **THEN** 输出符合 TAP 13 规范

#### Scenario: 退出码

- **WHEN** 全部通过
- **THEN** exit 0
- **AND** 任一失败 → exit 1
- **AND** 加载 / 解析失败 → exit 2
- **AND** 无测试 → exit 3

---

### Requirement: Benchmark 模式

#### Scenario: --bench 切换为基准模式

- **WHEN** `z42-test-runner . --bench`
- **THEN** 只跑 [Benchmark] 函数（默认跑 [Test]）

#### Scenario: Bencher.iter 闭包被多次调用

- **WHEN** [Benchmark] 函数 `fn bench_x(b: Bencher) { b.iter(|| { /* work */ }); }`
- **THEN** runner 自动 warmup（默认 3 次）+ 测时 N 次（默认 100 samples）
- **AND** 输出含 median + 95% CI

#### Scenario: --baseline 对比

- **WHEN** `--baseline main` 且 `bench/baselines/main-<os>.json` 存在
- **THEN** runner 输出每个 bench 与 baseline 的差异（≤ 5% 标 ≈，> 5% 标 ↑↓）

---

### Requirement: just 入口

#### Scenario: just test-changed 增量测试

- **WHEN** git diff 触及 src/libraries/z42.io/，执行 `just test-changed`
- **THEN** 只跑 z42.io tests + integration（不跑其他 stdlib）

#### Scenario: just test-stdlib z42.core

- **WHEN** `just test-stdlib z42.core`
- **THEN** 跑 src/libraries/z42.core/tests/ 下所有 .zbc

#### Scenario: just test-stdlib (无参)

- **WHEN** `just test-stdlib`（不传 lib 名）
- **THEN** 依次跑 6 个 stdlib 库 tests/

#### Scenario: just test-integration

- **WHEN** `just test-integration`
- **THEN** 跑 tests/integration/ 全部 .zbc

---

### Requirement: 工程集成

#### Scenario: workspace 集成

- **WHEN** 检查 src/runtime/Cargo.toml
- **THEN** [workspace] members 含 ../toolchain/test-runner

#### Scenario: 单独编译

- **WHEN** `cargo build -p z42-test-runner --release`
- **THEN** 产出 z42-test-runner binary，可独立调用

#### Scenario: 不影响 z42vm 主 build

- **WHEN** `cargo build --manifest-path src/runtime/Cargo.toml`
- **THEN** test-runner 不参与该 build（避免拖慢主路径）

---

### Requirement: 文档

#### Scenario: test-runner.md 含完整流程

- **WHEN** 阅读 [docs/design/test-runner.md](docs/design/test-runner.md)
- **THEN** 含：架构图 / 调度流程 / Bencher 协议 / Setup-Teardown 顺序 / 异常分类规则
