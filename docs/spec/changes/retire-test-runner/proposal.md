# Proposal: 退役 z42-test-runner，z42b test/bench GA

> 状态：DRAFT（前置：boxing 0.3.11 + Method.Invoke 非泛型 0.3.12）
> 里程碑：0.3.13（test）；bench 随删除一并迁移
> **修订（2026-06-25）**：命令宿主从 `z42c` 改为 **`z42b`（builder 编排器）**——test/bench =
> build+run+report = 编排，归 z42b（对标 `dotnet test` 在 driver 而非 `csc`）；on-device 面只能在
> z42b。runner 逻辑仍住 `z42.test`（库），z42b 是薄 verb。**bench 拉入本变更**：Rust test-runner
> 同时跑 [Test] 和 [Benchmark]，删它必须同时替掉两者，bench 不能滞后于删除。命令归属决策见
> [build-orchestrator.md](../../../design/toolchain/build-orchestrator.md)。

## Why

`z42-test-runner` 是一个独立的 Rust 二进制，承担 `[Test]` 测试发现、Setup/Teardown
生命周期、异常分类、多格式输出等职责。反射体系（C1–C3 + 扩展）全部落地、
Method.Invoke 在 0.3.12 实现后，这些职责可以在 z42 原生代码中表达——继续维护
Rust 二进制既违背自举方向，也是"语言验证自身"（dogfood）的缺口。

目标：**`z42b test` / `z42b bench` 端到端 GA，同时删除 `src/toolchain/test-runner/`**。

## What Changes

1. **`z42.test`**：新增反射驱动的 `TestRunner` 类（v2 API），通过 `MethodInfo`
   发现 `[Test]`/`[Setup]`/`[Teardown]` 方法，`Method.Invoke` 执行，汇总结果。
2. **`z42b`（builder 编排器）**：新增 `test` / `bench` 子命令——build（Compile 相位/ICompiler）
   → 调 `z42.test` 的 runner 库 → 按格式输出，非零 exit code 表示失败。命令是薄编排，逻辑在库。
3. **`z42.test`（bench 侧）**：[Benchmark] 发现 + `Bencher`（warmup/samples/stats）执行，供
   `z42b bench` 调用（与 [Test] 共用发现/反射基建）。
4. **xtask**：`z42 xtask.zpkg test` 内部切换到 `z42b test`（移除对 `z42-test-runner` 的调用）。
5. **test-runner 退役**：删除 `src/toolchain/test-runner/`，从 Cargo workspace 和 CI 移除。

## 前置依赖

| 前置 | 版本 | 原因 |
|------|------|------|
| boxing 机制 | 0.3.11 | `Method.Invoke(obj, args: object[])` 需要值类型自动装箱 |
| 非泛型 Method.Invoke | 0.3.12 | `TestRunner.Run` 用反射调用 `[Test]` 方法的执行路径 |

## Scope

**修改**：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.test/src/TestRunner.z42` | MODIFY | 新增 `TestRunner` v2（反射驱动），保留 v0 兼容 |
| `src/libraries/z42.test/src/TestResult.z42` | NEW | `TestResult`、`TestStatus` 值类型（pass/fail/skip） |
| `src/libraries/z42.test/src/TestDiscovery.z42` | NEW | `TestDiscovery.Discover(Type)` — 用反射找 `[Test]` 方法 |
| `src/libraries/z42.test/src/BenchRunner.z42` | NEW | [Benchmark] 发现 + Bencher 执行（bench 侧）|
| `src/toolchain/builder/core/builder_cli.z42` | MODIFY | `test` / `bench` verb 路由 + 调 runner 库 |
| `src/toolchain/builder/core/builder.z42` | MODIFY | test/bench 编排（build → 调 runner → 报告）|
| `src/libraries/z42.test/tests/runner_v2.z42` | NEW | TestRunner v2 端到端 [Test] |
| `src/toolchain/test-runner/` | DELETE | 整个目录删除 |
| `src/runtime/Cargo.toml` | MODIFY | 移除 test-runner workspace member |
| `src/toolchain/builder/core/z42.builder.z42.toml` | MODIFY | 确认 z42.test 在依赖中（test/bench 用）|

**只读引用**（不修改）：

- `src/toolchain/test-runner/src/runner.rs` — 理解 Setup/Teardown 生命周期语义
- `src/toolchain/test-runner/src/discover.rs` — TIDX 发现逻辑参考
- `docs/design/testing/testing.md` — 测试架构规范
- `src/runtime/src/corelib/reflection.rs` — Method.Invoke 接口参考

## Out of Scope

- **Timeout / 挂起检测**：延后至 0.4.x（需要 signal 处理或 async）
- **`--jobs N` 并行执行**：延后至 0.8.x（需要多线程）
- **JUnit XML / TAP 输出格式**：v1 只做 pretty + JSON；其余延后
- ~~Benchmark 模式~~ → **已拉入本变更**（What Changes #3）：Rust runner 同时跑 [Test]+[Benchmark]，删除必须同时替掉，bench 不能滞后
- **`[ShouldThrow<E>]` 继承链匹配**：按现有反射能力实现简单类型匹配
- **z42b 全量命令面落地**（build/publish/export/run 等）：独立变更（build-orchestrator）；本次只加 test/bench 两个 verb
- **on-device test 面**（`--plat` 导出 harness 上设备跑）：延后至 workload B + export 就绪（host 面先 GA）

## Open Questions

- [ ] z42b test 的 `--format` 默认值：pretty（TTY）还是 JSON（CI）？
      → 建议：tty 检测，TTY 时 pretty，管道时 JSON（对齐 cargo test）
- [ ] xtask 调用 z42b test 是 in-process 还是 subprocess？
      → 建议：subprocess（`z42b test <zbc> --format json`），结果解析由 xtask 聚合；
        后续 in-process 优化留延后
- [ ] z42b test 接受 .zbc 还是 .z42.toml？
      → 建议：两者都支持；.z42.toml 先 build 再 test（对齐 `dotnet test`）
- [ ] bench 基线/diff：复用 `xtask bench --diff` + `bench-baselines` 分支，还是 z42b 内建？
      → 建议：复用现有门禁，z42b bench 产出对齐其格式
