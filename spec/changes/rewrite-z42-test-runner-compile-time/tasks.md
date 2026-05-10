# Tasks: Rewrite z42-test-runner with Compile-Time Discovery

> 状态：🟡 实施中 | 创建：2026-04-29 | 启动：2026-05-10
> 依赖 R1 + R2 + R4 + A2/A3 + R3a/c — 全部 ✅ 已落地

## 进度概览

- [ ] 阶段 0: PoC — 验证 in-process Setup→Test→Teardown 三调用 +  Bencher closure stash/take 接口
- [ ] 阶段 1: 拆 main.rs → 模块（discover/runner/result/format/）
- [ ] 阶段 2: discover in-process + zpkg 输入支持
- [ ] 阶段 3: runner Setup/Teardown 调度生效
- [ ] 阶段 4: bencher.rs (warmup + 统计 + closure 协议)
- [ ] 阶段 5: format module 化（保留现有 TAP/JSON/pretty 三种）
- [ ] 阶段 6-7: scripts + justfile（R3c 已落地，仅检查/小修）
- [ ] 阶段 8: 文档
- [ ] 阶段 9: 验证

---

## 阶段 0: PoC（先验证关键不确定性）

> 目的：在大改 main.rs 之前确认两个核心机制能跑通，避免实施期 redesign。

- [ ] 0.1 验证 in-process API：手写小 PoC，从 host 调 `interp::run(ctx, module, fn_name1, &[])` 然后 `interp::run(ctx, module, fn_name2, &[])`，两个 fn 共享 VmContext 的 static_fields 与 sink state。证明 Setup → Test → Teardown 三调用流程可行
- [ ] 0.2 验证 Bencher closure stash/take：复用 R2 的 Bencher 类，让 z42 [Benchmark] fn 调 `bencher.Iter(closure)`；runner 通过 thread-local 把闭包提取出来反向多次调用并测时。证明 R2 Bencher native impl 支持这个反向 dispatch（必要时小改 R2 native）
- [ ] 0.3 PoC 结果记录到本 tasks.md 备注区；如发现接口不足，停下评估是否需要先扩 R2 / runtime API

## 阶段 1: 拆 main.rs → 模块

- [ ] 1.1 `src/toolchain/test-runner/Cargo.toml`：加 `regex` 依赖（spec 要求 regex filter，现 substring）
- [ ] 1.2 `src/toolchain/test-runner/src/result.rs` (NEW)：从 main.rs 提取 `TestStatus` / `TestResult` / `TestSuiteResult`
- [ ] 1.3 `src/toolchain/test-runner/src/discover.rs` (NEW)：提取 `TestReport::from_artifact` + `DiscoveredTest`
- [ ] 1.4 `src/toolchain/test-runner/src/format/{mod,tap,json,pretty}.rs` (NEW)：拆 main.rs 现有三个 emit 函数到 trait + 实现
- [ ] 1.5 `src/toolchain/test-runner/src/main.rs` (MODIFY)：缩到 ~80 行 CLI 入口 + 调度；旧 monolithic 内容已搬到模块
- [ ] 1.6 `cargo build -p z42-test-runner` + 现有测试 pass — 重构无行为变化

## 阶段 2: discover 扩展

- [ ] 2.1 `discover.rs::collect_artifacts` (NEW)：支持目录递归扫描 + zpkg 输入（packed mode）
- [ ] 2.2 `--filter` 改为 regex（依赖 R2 加的 regex crate）
- [ ] 2.3 收集 Setup / Teardown method_ids 进 `DiscoveredTest`（同 module 内）

## 阶段 3: Runner — in-process + Setup/Teardown

- [ ] 3.1 `runner.rs::run_one(test, artifact, sink_buf)` (NEW)：单 [Test] 执行（替代 subprocess fork）
- [ ] 3.2 sink 安装 + 取出（调 R2 的 `__test_io_install_stdout_sink` / `__test_io_take_stdout_buffer` builtin）
- [ ] 3.3 Setup → 主体 → Teardown 顺序调度（共享 VmContext）；任一抛出按异常类型分类
- [ ] 3.4 异常分类（TestFailure / SkipSignal / 其他 / ShouldThrow 期望，与 A2/A3 chain 兼容）
- [ ] 3.5 TestCase 参数化展开（如 R1 TIDX 含 TestCases；当前不必，留 follow-up）

## 阶段 4: Bencher

- [ ] 4.1 [src/toolchain/test-runner/src/bencher.rs](src/toolchain/test-runner/src/bencher.rs) run_benchmark() 函数
- [ ] 4.2 closure 提取协议（thread-local stash + take）
- [ ] 4.3 warmup + samples + median + IQR 计算
- [ ] 4.4 与 P1 baseline-schema.json 兼容（输出可被 bench-diff.sh 消费）

## 阶段 5: Formatter

- [ ] 5.1 [src/toolchain/test-runner/src/format/mod.rs](src/toolchain/test-runner/src/format/mod.rs) Formatter trait
- [ ] 5.2 [src/toolchain/test-runner/src/format/tap.rs](src/toolchain/test-runner/src/format/tap.rs) TAP 13
- [ ] 5.3 [src/toolchain/test-runner/src/format/json.rs](src/toolchain/test-runner/src/format/json.rs) JSON
- [ ] 5.4 [src/toolchain/test-runner/src/format/pretty.rs](src/toolchain/test-runner/src/format/pretty.rs) TTY 友好（colored）
- [ ] 5.5 默认 format：TTY 自动 pretty，否则 tap

## 阶段 6-7: scripts + justfile（R3c 已落地，仅检查）

- [ ] 6.1 检查 `scripts/test-changed.sh` 已存在 + 行为符合预期
- [ ] 7.1 检查 `justfile` 的 `test-changed` / `test-stdlib` / `test-integration` 不再是占位（如仍是占位则替换）

## 阶段 8: 文档

- [ ] 8.1 [docs/design/test-runner.md](docs/design/test-runner.md) 完整设计
- [ ] 8.2 [docs/design/testing.md](docs/design/testing.md) 加 R3 段（runner 在大图中位置）
- [ ] 8.3 [src/toolchain/test-runner/README.md](src/toolchain/test-runner/README.md) 用法 + 示例
- [ ] 8.4 [docs/dev.md](docs/dev.md) 加 "z42-test-runner" 段

## 阶段 9: 验证

- [ ] 9.1 `cargo build -p z42-test-runner --release` 通过
- [ ] 9.2 [src/toolchain/test-runner/tests/integration_test.rs](src/toolchain/test-runner/tests/integration_test.rs) 单测全绿
- [ ] 9.3 手动：写一个 demo .z42（含 [Test] 通过/失败/skip/should_throw 各 1）→ 编译 → runner 跑 → 输出符合 spec
- [ ] 9.4 --format tap / --format json / --format pretty 各自正确
- [ ] 9.5 --filter 过滤生效
- [ ] 9.6 --bench 模式跑通至少 1 个 [Benchmark]
- [ ] 9.7 退出码：通过=0、失败=1、错误=2、无测试=3
- [ ] 9.8 现有 just test 全绿（不影响 vm_core / cross-zpkg）
- [ ] 9.9 just test-stdlib z42.test 跑通 z42.test 自身（dogfooding 占位 OK）

## 备注

### 实施依赖

- R1 (test metadata section) 必须先落地
- R2 (z42.test library) 必须先落地（runner 调用 R2 的 native）
- 与 R4 / R5 无依赖

### 风险（2026-05-10 update）

- ~~**风险 1**：Interpreter 公开 API 不足~~ → ✅ **已解**：`interp::run(ctx, module, fn, args)` + `interp::run_returning` 已是公开 API
- **风险 2**：Closure value 提取 + 调用协议未定 → 阶段 0 PoC 验证（先行确认）
- ~~**风险 3**：异常类型识别用字符串 contains~~ → ✅ **A3 chain 已解**：编译期把 [ShouldThrow<E>] 的 E + 派生类短名写入 TIDX；runner split 后任一命中即 Pass，无需 type 反射
- **风险 4**：CI 上 runner 跑慢 → 串行 v0.1 接受；v0.2 加并行

### 工作量估计

3-4 天：
- crate + 接口骨架：0.5 天
- discover + result：0.5 天
- runner 主流程（sink + Setup-Teardown + 异常分类）：1 天
- Bencher：0.5 天
- Formatter：0.5 天
- scripts/test-changed.sh + justfile + 文档：0.5 天
- 集成验证 + 调试：0.5 天
