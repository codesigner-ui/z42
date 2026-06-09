# Tasks: fix-dist-runner-test-dirs

> 状态：🟢 已完成并归档（2026-06-09）

**变更说明：** dist 测试 runner（`z42 xtask.zpkg test dist`）的"lib 测试 dir-mode"发现会把 `src/libraries/<lib>/tests/<name>/source.z42` 当 golden 用例编译运行；但其中 `[Test]`/`[Benchmark]`-based 的 dir（由 z42-test-runner 经 `test lib` 跑、无 Main、可能多文件如 secp256k1 的 source.z42 + vectors.z42）不是 golden-runnable → 单文件编译 source.z42 跑挂（如 `secp256k1` FAIL）。

**原因：** z42 写的 dist runner 缺少 C# `GoldenTests` 已有的排除逻辑（`ContainsTestRunnerAttribute`）—— 后者跳过 `LibrariesRoot` 下含 `[Test]`/`[Benchmark]` 的 source.z42。dist runner 需镜像同一排除。

**文档影响：** 无（纯测试 harness fix，不改外部行为 / 机制 / 约定；归档备注即可）。

**子系统锁：** `toolchain`（与 port-z42c-core 并行，User 授权 2026-06-09；文件不重叠：本 fix 改 `xtask_test_dist.z42`，port 改 compiler-z42 test channel）。

- [x] 1.1 `scripts/xtask_test_dist.z42`：加 `_isTestRunnerSource(src)`（镜像 `ContainsTestRunnerAttribute`：逐行 trim，行首 `[Test]`/`[Test(`/`[Benchmark]`/`[Benchmark(` 即 true）
- [x] 1.2 `_enumerateDistCases` Dir mode part 2（lib tests）：`File.Exists(src) && !_isTestRunnerSource(src)` 才纳入
- [x] 1.3 rebuild xtask.zpkg ✓；dist 运行确认 `secp256k1` 已从用例集消失
- [x] 1.4 **直接验证（robust to gate 噪声）**：20 个 lib-test source.z42 dir 中恰 1 个（secp256k1）是 [Test]-based → 排除；其余 19 个 Main-based golden（z42.math / z42.collections）零 [Test] 行 → 保留。与 C# GoldenTests 一致。secp256k1 仍由 `test lib`（z42-test-runner）覆盖，不丢覆盖。
- [x] 1.5 commit + push + 归档 + 释放 toolchain 并行占用

## 备注
- **clean full `test dist` 未取得**：验证期环境有并行 test 运行（User 的 `test stdlib` / `test vm` / `test compiler-z42`，PID 2995/32100/63522）正重建 `z42.core.zpkg` 等共享产物 → 编译期 "catch type 'Exception' not found" 全盘假失败（与本 fix 无关）。本 fix 的正确性由 1.4 的全量 lib-test dir 直接枚举核验（确定性，独立于受污染的 dist 运行）。环境安静后 `test dist` 即恢复（secp256k1 不再误失败）。
