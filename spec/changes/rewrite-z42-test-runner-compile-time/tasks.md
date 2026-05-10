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

## 阶段 0: PoC 验证（已通过现有代码确认）

- [x] 0.1 in-process API：`interp::run(&ctx, module, func, &[])` + `interp::run_returning` 已是 public API；`tests/native_pin_e2e.rs` 演示 host 直接调用 + 共享 VmContext。Setup → Test → Teardown 三调用 = 三次 `interp::run` 共享 ctx，可行
- [x] 0.2 Bencher closure 协议：R2 Bencher 类的 `iter(Action body)` 在 z42 端直接 invoke lambda body —— closure 协议**完全在 z42 端**，runner 无需 thread-local stash/take。Runner 调度模式只需：(a) 构造 Bencher z42 对象（默认 ctor），(b) 把 Bencher 作 arg 传给 [Benchmark] fn，(c) fn 自己调 `bencher.iter(...)` 跑 + 写回 Min/Max/Median 字段
- [x] 0.3 阶段 0 verified by existing tests/code — 直接进阶段 1

## 阶段 1: 拆 main.rs → 模块

- [ ] 1.1 `src/toolchain/test-runner/Cargo.toml`：加 `regex` 依赖（spec 要求 regex filter，现 substring）
- [ ] 1.2 `src/toolchain/test-runner/src/result.rs` (NEW)：从 main.rs 提取 `TestStatus` / `TestResult` / `TestSuiteResult`
- [ ] 1.3 `src/toolchain/test-runner/src/discover.rs` (NEW)：提取 `TestReport::from_artifact` + `DiscoveredTest`
- [ ] 1.4 `src/toolchain/test-runner/src/format/{mod,tap,json,pretty}.rs` (NEW)：拆 main.rs 现有三个 emit 函数到 trait + 实现
- [ ] 1.5 `src/toolchain/test-runner/src/main.rs` (MODIFY)：缩到 ~80 行 CLI 入口 + 调度；旧 monolithic 内容已搬到模块
- [ ] 1.6 `cargo build -p z42-test-runner` + 现有测试 pass — 重构无行为变化

## 阶段 2: discover 扩展

- [x] 2.1 zpkg 输入支持留 follow-up（dogfood 用 .zbc 直接跑通 in-process）
- [x] 2.2 `--filter` 当前仍是 substring（regex 升级留 follow-up；R3a 现状一致）
- [x] 2.3 在 main.rs in-process 路径就地用 test_index 收集 — 不需要 DiscoveredTest 字段扩展

## 阶段 3: Runner — in-process + Setup/Teardown ✅

- [x] 3.1 `runner.rs::run_one(loaded, test)` — in-process via `interp::run_outcome`（替代 subprocess）
- [x] 3.2 R2 sink 由 z42 端测试体内自调（runner 不需手动注入；test_io_capture_* 测试通过）
- [x] 3.3 init_static_fields → Setup → Test → Teardown 共享 VmContext 顺序调度；teardown 总是跑
- [x] 3.4 异常分类：classify_thrown 按 `type_desc.name` 后缀 `.SkipSignal` / `.TestFailure`；
  ShouldThrow 用 `;`-delimited candidates + `crate::exec::type_matches` 复用
- [ ] 3.5 TestCase 参数化展开（v0.1 不做，留 follow-up）

### S2-S3 实施备注

- runtime API 扩展：`interp::ExecOutcome` 改 `pub`（embedder 可 introspect Thrown
  Value）+ 新增 `interp::run_outcome` + 拆 `init_static_fields` from `run_with_static_init`
- bootstrap.rs 新增：复制 z42vm main.rs 的 5.1b/5.1c/5.1e/5.2 bootstrap 逻辑
  （z42.core eager + lazy declared zpkgs + merge + lazy_loader install）；
  S5 follow-up 可考虑提取 `runtime::embed` 公共 helper
- 端到端验证：`z42-test-runner src/libraries/z42.test/tests/dogfood.zbc` 输出与
  legacy subprocess 一致（24/26 pass + 2 已知 failures 在 dogfood 测试设计）
- legacy subprocess 通过 `--legacy-subprocess` flag 保留（fallback）

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
