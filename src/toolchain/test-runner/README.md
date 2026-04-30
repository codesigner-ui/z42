# z42-test-runner

z42 测试运行器（R3 minimal 实施）。读取 .zbc 中的 TIDX section（spec R1
[`add-test-metadata-section`](../../../spec/archive/2026-04-30-add-test-metadata-section/)），调度
`[Test]` 函数执行，分类输出结果。

## 当前能力（R3 minimal + A2 + A3 + R3a）

- ✅ 加载单个 .zbc，读 TIDX section
- ✅ subprocess fork z42vm per [Test]（每个测试 fresh VM 状态）
- ✅ `[Skip(reason: ...)]` 编译时跳过 + `Std.Test.Assert.Skip(...)` 运行时跳过
- ✅ `[Ignore]` 静默忽略
- ✅ 异常分类：`TestFailure` / `SkipSignal` / 其他 Exception → 失败
- ✅ `[ShouldThrow<E>]` runtime 比对（A2）+ inheritance-aware（A3 编译期 chain）
- ✅ `--format <pretty|tap|json>` 输出（默认 TTY-aware）
- ✅ `--filter <SUBSTR>` substring 过滤
- ✅ 退出码：0 全过 / 1 有失败 / 2 工具错误 / 3 无测试

## 推迟项（受其他 spec 阻塞）

| 功能 | 阻塞原因 | 落地 spec |
|------|---------|----------|
| in-process 执行（去 fork） | 需 z42-runtime 拆 lib + LazyLoader 集成 | R3b |
| `[Setup]` / `[Teardown]` 真生效 | 跨 subprocess 无法共享状态 | R3b（与 in-process 同) |
| `--bench` 模式 | `Bencher.iter(closure)` 需 closure | R2.C |
| `[TestCase(args)]` 参数化 | parser 需 typed args | R4+ |
| 并行执行 | 需要 thread-safe Interpreter | R6 / v0.2 |
| 多 .zbc 同时跑 | 需要 .zbc 路径递归 + 命名空间合并 | R3b |
| stdout 捕获（默认隐藏 PASS 输出） | `TestIO.captureStdout(closure)` 需 closure | R2.B |
| `--tag` filter | 需 z42 attribute 加 tag 字段 | 独立 spec |
| regex filter | 当前 substring 够用 | 真有需求时升级 |
| 增量测试（`scripts/test-changed.sh`） | git diff → 反向依赖图 | R3c |

## 使用

```bash
# 编译 .zbc（含 [Test] 函数）
dotnet run --project src/compiler/z42.Driver -- examples/test_demo.z42 --emit zbc -o /tmp/test_demo.zbc

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

- 输入契约：[`docs/design/zbc.md` 的 TIDX section](../../../docs/design/zbc.md#tidx-test-index可选spec-r1)
- 异常类型：[`Std.Test.TestFailure` / `Std.Test.SkipSignal`](../../libraries/z42.test/src/Failure.z42)
- Assert API：[`Std.Test.Assert`](../../libraries/z42.test/src/Assert.z42)
- 测试框架总览：[`docs/design/testing.md`](../../../docs/design/testing.md)

## 实施记录

| Phase | Commit | 范围 |
|-------|--------|------|
| R3 minimal | (本次) | 单 .zbc CLI + discovery + Setup/Teardown 调度 + 异常分类 + pretty 输出 |
