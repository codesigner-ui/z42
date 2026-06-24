# Design: 退役 z42-test-runner

> **修订（2026-06-25）**：命令宿主 `z42c` → **`z42b`（builder）**；bench 与 test 同批迁移
> （Rust runner 同跑两者，删除须同时替）。runner 逻辑仍住 `z42.test`（库，必须是库——
> on-device test harness 要把它打进设备 app）；z42b 是薄编排 verb（build → 调库 → 报告），
> 与 Trim 等 stage 同构。决策见 [build-orchestrator.md](../../../design/toolchain/build-orchestrator.md)。

## Architecture

```
Before (0.3.12 及之前):
  z42 xtask.zpkg test vm
    └─ z42-test-runner <lib.zbc> [--format json]   ← Rust 二进制
         ├─ TIDX 发现（编译期元数据）
         ├─ in-process Setup/Test/Teardown 调用
         └─ pretty / JSON / TAP / JUnit XML 输出

After (0.3.13):
  z42 xtask.zpkg test vm
    └─ z42b test <lib.zbc> --format json            ← z42 原生（薄编排 verb）
         └─ z42.test.TestRunner (反射驱动，住库)
              ├─ TestDiscovery.Discover(Type)         反射发现 [Test]
              ├─ MethodInfo.Invoke(obj, args)         反射执行
              └─ TestResult[] → JSON 输出
  z42b bench <lib.zbc>                               ← 同构：发现 [Benchmark] → Bencher → stats
```

## TestRunner v2 API

```z42
// z42.test.TestRunner — v2（反射驱动）
public class TestRunner {
    // 发现并执行 testType 上的所有 [Test] 方法
    // 返回每个测试的结果（含 pass/fail/skip + 异常信息）
    public static TestResult[] Run(Type testType);

    // 执行单个 [Test] 方法（含 [Setup] + [Teardown] 生命周期）
    public static TestResult RunOne(MethodInfo method, Type testType);
}

public class TestResult {
    public string   Name;       // 方法名
    public string   FullName;   // 类名.方法名
    public TestStatus Status;   // Pass / Fail / Skip
    public string   ErrorMsg;   // "" if Pass
    public string   ErrorType;  // exception type name, "" if Pass
}

public enum TestStatus { Pass, Fail, Skip }   // 待 enum 作类型实体后替换为 enum
// 暂用 string 常量（"pass" / "fail" / "skip"）直到 IsEnum 落地
```

## TestDiscovery

```z42
// z42.test.TestDiscovery — 用反射发现 [Test] / [Setup] / [Teardown]
public class TestDiscovery {
    // 返回 testType 上所有标注 [Test] 的 MethodInfo
    public static MethodInfo[] FindTests(Type t);
    // 返回 [Setup] 方法列表（按名排序，确定顺序）
    public static MethodInfo[] FindSetups(Type t);
    // 返回 [Teardown] 方法列表
    public static MethodInfo[] FindTeardowns(Type t);
}
```

## `z42b test` / `z42b bench` 命令设计

```
z42b test  <file.zbc|file.z42.toml> [--filter <substr>] [--format pretty|json] [--list]
z42b bench <file.zbc|file.z42.toml> [--filter <substr>] [--format pretty|json] [--diff] [--baseline <ref>]
```

- **`<file.zbc>`**：直接对已编译产物运行
- **`<file.z42.toml>`**：先 build 再跑（编排器复用 Compile 相位/ICompiler，等同 `z42b build` + 运行）
- **`--filter`**：名字子串过滤；**`--list`**：只列不执行
- **`--format pretty`**（默认 TTY）/ **`json`**（管道/CI）
- **test Exit code**：0=全通，1=有失败，2=参数错误，3=编译失败
- **bench 专属**：`--diff`/`--baseline` 复用现有 `xtask bench --diff` + `bench-baselines` 分支门禁；
  输出对齐其格式（warmup/samples/median + 阈值回归判定）

> test/bench 都是 z42b 的薄 verb：解析参数 → （必要时）build → 调 `z42.test` 的
> `TestRunner` / `BenchRunner` 库 → 格式化输出。逻辑零重复，命令仅编排。

## Decisions

### D1: TestRunner 如何获取 Type 对象

**问题**：`z42b test <lib.zbc>` 加载后，如何枚举类型并找 `[Test]`？

**选项**：
- A：依赖 TIDX 编译期元数据（同 test-runner），只是调用改为 z42 代码
- B：纯反射——加载 .zbc 后用某个入口枚举所有类型，对每个类型调 `GetMethods()` 找 `[Test]`
- C：约定入口——编译期写一个 `__test_entry__` 函数，列出所有测试类型

**决定**：选 **A（沿用 TIDX + 反射协同）**。
理由：TIDX 元数据已存在且可靠；纯反射需要"枚举模块内所有 Type"的 API（尚未实现）；
约定入口会增加编译器工作量。使用路径：TIDX 提供方法 ID → 用反射找对应 `Type` →
调 `TestDiscovery.FindTests(Type)` 验证（可作 assert）。

实际执行路径：
1. z42b test 通过 `Std.Runtime.LoadModule(path)` 加载 .zbc
2. 遍历 TIDX 条目得到测试方法 FQN
3. 用 `Type.GetType(fqn)` 拿到 `Type` 对象（需新增该 API）
4. 用 `TestDiscovery.FindTests(Type)` 拿 MethodInfo 列表
5. 逐个 `TestRunner.RunOne(method, type)`

### D2: TestStatus 用 string 还是 int 常量

**问题**：`enum TestStatus { Pass, Fail, Skip }` 依赖 IsEnum（0.3.12 届时才有）。

**决定**：v2 临时用 `string` 常量（`"pass"/"fail"/"skip"`），IsEnum 落地后在
同一个 commit 内改 enum（改动面极小）。

### D3: Setup/Teardown 生命周期

保留 test-runner 的语义：
- 每个 `[Test]` 方法执行前：顺序执行所有 `[Setup]` 方法
- 每个 `[Test]` 方法执行后：顺序执行所有 `[Teardown]` 方法（无论 test 是否失败）
- `[Setup]` 失败 → 跳过该 test，状态 = Skip（not Fail）
- 测试共享同一 Type 实例（每次 RunOne 重新 new 实例）

### D4: 静态字段重置

test-runner 在 in-process 模式下每个测试前调用 `interp::init_static_fields()`。

z42b test 以 subprocess 方式调用（D3 决定），每个 subprocess 是干净进程，
天然隔离——无需显式重置。性能牺牲可接受（每测试 ~50ms 启动），与现有
test-runner `--legacy-subprocess` 模式同级。

后续 in-process 优化可在 VM 暴露 `--reset-statics` 后回填。

### D5: xtask 集成方式

xtask 目前在 z42 中通过 `z42-test-runner <zbc>` 调 Rust 二进制。
改为：`z42b test <zbc> --format json`，子进程输出 JSON 由 xtask 解析（同现有 JSON 格式）。

JSON 格式对齐：`{"name": "...", "status": "pass|fail|skip", "error": "...", "ms": N}`

## Testing Strategy

- `src/libraries/z42.test/tests/runner_v2.z42` — `[Test]` 用自己跑自己（bootstrap 测试）
- 在 xtask 里加一个 `test lib` 阶段确认 z42b test 替代品工作（22 lib 测试全通）
- retirement 标志：`z42 xtask.zpkg test lib` 改用 z42b test 后，test-runner 二进制
  从 cargo build 产物中消失

## Deferred

### retire-test-runner-future-timeout
- **触发原因**：z42 无 signal 处理，无法 per-test 超时
- **前置依赖**：async/signal 机制（0.4.x）或 VM `--timeout` 参数
- **触发条件**：0.4.x 测试框架 v1 规划时
- **当前 workaround**：CI job 层 timeout（github workflow `timeout-minutes`）

### retire-test-runner-future-parallel
- **触发原因**：z42 单线程，`--jobs N` 并行不可做
- **前置依赖**：多线程（0.8.x）
- **触发条件**：0.8.x async/thread 规划时
- **当前 workaround**：xtask 在 Rust 侧 per-lib 并行（不受影响）

### retire-test-runner-future-tap-junit
- **触发原因**：TAP 13 和 JUnit XML 格式本次不实现
- **前置依赖**：无，纯工作量
- **触发条件**：有 CI 集成需要时

### ~~retire-test-runner-future-benchmark~~ → **已拉入本变更（2026-06-25）**
- Rust test-runner 同时跑 [Test]+[Benchmark]，删除必须同时替掉两者 → bench 不能延后。
- `z42.test` 加 `BenchRunner`（发现 [Benchmark] + Bencher warmup/samples/stats），`z42b bench` 调用；
  基线/diff 复用 `xtask bench --diff` + `bench-baselines` 分支。
