# z42-test-runner

z42 测试运行器（R3 minimal 实施）。读取 .zbc 中的 TIDX section（spec R1
[`add-test-metadata-section`](../../../spec/archive/2026-04-30-add-test-metadata-section/)），调度
`[Test]` 函数执行，分类输出结果。

## 当前能力（R3 minimal）

- ✅ 加载单个 .zbc，读 TIDX section
- ✅ 顺序执行 `[Test]` 函数（fresh `VmContext` 每个测试）
- ✅ `[Setup]` / `[Teardown]` 调度（每个 test 前后调用，teardown 必跑）
- ✅ `[Skip(reason: ...)]` 编译时跳过 + `Std.Test.Assert.Skip(...)` 运行时跳过
- ✅ `[Ignore]` 静默忽略
- ✅ 异常分类：`TestFailure` / `SkipSignal` / 其他 Exception → 失败
- ✅ Pretty 输出 + TTY 颜色检测
- ✅ 退出码：0 全过 / 1 有失败 / 2 工具错误 / 3 无测试

## 推迟项（受其他 spec 阻塞）

| 功能 | 阻塞原因 | 落地 spec |
|------|---------|----------|
| TAP / JSON formatter | (no blocker，时间) | R3 完整版 |
| `--filter <regex>` / `--tag` | 同上 | R3 完整版 |
| `--bench` 模式 | `Bencher.iter(closure)` 需 closure | R2.C + R3 完整版 |
| `[ShouldThrow<E>]` 处理 | parser 需 generic attribute syntax | R4 |
| `[TestCase(args)]` 参数化 | parser 需 typed args | R4 |
| 并行执行 | 需要 thread-safe Interpreter | R6 / v0.2 |
| 多 .zbc 同时跑 | 需要 .zbc 路径递归 + 命名空间合并 | R3 完整版 |
| stdout 捕获（默认隐藏 PASS 输出） | `TestIO.captureStdout(closure)` 需 closure | R2.B |
| 跨平台 baseline diff | 与 P1.D bench 流复用 | R3 完整版 + P1 |

## 使用

```bash
# 编译 .zbc（含 [Test] 函数）
dotnet run --project src/compiler/z42.Driver -- examples/test_demo.z42 --emit zbc -o /tmp/test_demo.zbc

# 跑测试
cargo run -p z42-test-runner --release -- /tmp/test_demo.zbc
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

- 输入契约：[`docs/design/zbc.md` 的 TIDX section](../../../docs/design/zbc.md#tidx-test-index可选spec-r1)
- 异常类型：[`Std.Test.TestFailure` / `Std.Test.SkipSignal`](../../libraries/z42.test/src/Failure.z42)
- Assert API：[`Std.Test.Assert`](../../libraries/z42.test/src/Assert.z42)
- 测试框架总览：[`docs/design/testing.md`](../../../docs/design/testing.md)

## 实施记录

| Phase | Commit | 范围 |
|-------|--------|------|
| R3 minimal | (本次) | 单 .zbc CLI + discovery + Setup/Teardown 调度 + 异常分类 + pretty 输出 |
