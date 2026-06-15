# z42-test-runner

z42 测试运行器。读取 .zbc 中的 TIDX section（spec R1
[`add-test-metadata-section`](../../../spec/archive/2026-04-30-add-test-metadata-section/)），调度
`[Test]` 函数执行，分类输出结果。

## 当前能力

- ✅ 加载单个 .zbc，读 TIDX section
- ✅ **默认 in-process 执行**（R3b）：共享 `VmContext`、per-test 重置静态字段，
  调用 `interp::run_outcome` 直跑，无 fork 开销
- ✅ `[Setup]` / `[Teardown]` hook 真生效（in-process 模式下；按 TIDX kind 顺序执行）
- ✅ `--jobs N`（N>1）并行执行：worker pool 跑 subprocess（`VmContext` 是 `!Send`，
  故并行强制走 subprocess；**此模式下 Setup/Teardown 不生效**）
- ✅ `--legacy-subprocess`：回退到 fork z42vm per [Test]（兼容/排障用）
- ✅ `--bench` 基准模式：`Bencher` 测量 + `BenchStats` 汇报
- ✅ `[Skip(reason: ...)]` 编译时跳过 + `Std.Test.Assert.Skip(...)` 运行时跳过
- ✅ `[Ignore]` 静默忽略
- ✅ stdout 捕获（`TestIO.captureStdout`）+ PASS 输出默认隐藏
- ✅ 异常分类：`TestFailure` / `SkipSignal` / 其他 Exception → 失败
- ✅ `[ShouldThrow<E>]` runtime 比对（A2）+ inheritance-aware（A3 编译期 chain）
- ✅ `--format <pretty|tap|json>` 输出（默认 TTY-aware）
- ✅ `--filter <SUBSTR>` substring 过滤
- ✅ 退出码：0 全过 / 1 有失败 / 2 工具错误 / 3 无测试

## 推迟项（受其他 spec 阻塞）

| 功能 | 阻塞原因 | 落地 spec |
|------|---------|----------|
| 多 .zbc 单次同时跑 | 需要 .zbc 路径递归 + 命名空间合并（当前由 xtask per-zpkg 调度） | R3b 残留 |
| 并行 + Setup/Teardown 共存 | `VmContext` `!Send`，并行只能 subprocess；需 thread-safe Interpreter | R6 / v0.2 |
| `[TestCase(args)]` 参数化 | parser 需 typed args | R4+ |
| `--tag` filter | 需 z42 attribute 加 tag 字段 | 独立 spec |
| regex filter | 当前 substring 够用 | 真有需求时升级 |
| 增量测试（`z42 xtask.zpkg test changed`） | git diff → 反向依赖图 | R3c |

## 使用

```bash
# 编译 .zbc（含 [Test] 函数）
dotnet run --project src/compiler/z42.Driver -- src/runtime/tests/data/test_demo/source.z42 --emit zbc -o /tmp/test_demo.zbc

# 跑测试（默认 pretty）
cargo run -p z42-test-runner --release -- /tmp/test_demo.zbc

# CI / 机器消费
cargo run -p z42-test-runner --release -- /tmp/test_demo.zbc --format json > report.json
cargo run -p z42-test-runner --release -- /tmp/test_demo.zbc --format tap | tap-junit-converter

# 子集筛选（substring match on method name）
cargo run -p z42-test-runner --release -- /tmp/test_demo.zbc --filter assert_equal
```

## 输出示例

```
running 6 tests from test_demo.zbc

  ✓ test_simple  (0ms)
  ⊘ test_skipped_unconditional  (blocked by issue #123)
  ⊘ test_skipped_on_ios  (JIT not supported on iOS)
  ⊘ test_skipped_without_feature  (single-threaded build)
  ⊘ test_skipped_combined  (wasm sandbox has no fs)
  ✓ test_ignored ...   # 实际不会显示，[Ignore] 静默跳过

result: ok.  1 passed; 0 failed; 5 skipped
```

## 设计与契约

- 输入契约：[`docs/design/runtime/zbc.md` 的 TIDX section](../../../docs/design/runtime/zbc.md#tidx-test-index可选spec-r1)
- 异常类型：[`Std.Test.TestFailure` / `Std.Test.SkipSignal`](../../libraries/z42.test/src/Failure.z42)
- Assert API：[`Std.Test.Assert`](../../libraries/z42.test/src/Assert.z42)
- 测试框架总览：[`docs/design/testing/testing.md`](../../../docs/design/testing/testing.md)

## 实施记录

| Phase | 范围 |
|-------|------|
| R3 minimal | 单 .zbc CLI + discovery + 异常分类 + pretty 输出（subprocess fork per test） |
| R3a + A2/A3 | `[ShouldThrow<E>]` runtime 比对 + inheritance-aware；TAP/JSON 格式 |
| R3b（默认 in-process） | `interp::run_outcome` 直跑，去 fork；`[Setup]`/`[Teardown]` 真生效；stdout 捕获 |
| add-test-runner-parallel | `--jobs N`（N>1）subprocess worker pool；`--legacy-subprocess` 回退 |
