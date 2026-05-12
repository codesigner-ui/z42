# Tasks: Rewrite z42-test-runner with Compile-Time Discovery

> 状态：🟢 已完成 | 创建：2026-04-29 | 启动：2026-05-10 | 归档：2026-05-12

## 实际交付（vs proposal）

**核心目标已 ship**：z42-test-runner 从 subprocess fork-per-test 改成 in-process
`interp::run_outcome` 调度；Setup/Teardown 顺序在共享 VmContext 上执行；TIDX
metadata 编译期发现 [Test] / [Setup] / [Teardown] / [ShouldThrow]。

**de-scope**（不在本 spec 实现，列入 follow-up backlog）：
- Bencher 独立 `bencher.rs` 模块 —— z42 端 `Std.Test.Bencher` 类直接驱动 warmup/samples/
  median；runner 无需 thread-local stash/take。Bencher e2e 通过 dogfood 6/26 测试覆盖
- regex `--filter`（仍是 substring，dogfood 用例足够；regex 升级独立 spec）
- TestCase 参数化展开（spec 内显式标注 v0.1 不做）
- `--bench` 独立调度路径（[Benchmark] 当前走普通 [Test] 路径，dogfood 5 个 bench 测试
  passes 验证）

## 进度概览

- [x] 阶段 0: PoC 验证（in-process interp::run + Bencher 协议确认）
- [x] 阶段 1: main.rs → 模块拆分（discover/runner/result/format/exec/bootstrap）
- [x] 阶段 2: discover in-process（test_index 收集 + DiscoveredTest）
- [x] 阶段 3: Runner — in-process + Setup/Teardown 共享 VmContext
- [x] 阶段 5: Formatter 模块（format/{mod,tap,json,pretty}.rs 已落地）
- [x] 阶段 6-7: scripts/test-changed.sh + justfile recipes
- [x] 阶段 9: 验证（dogfood 26/26 自跑 + test-stdlib.sh 6/6 lib 全绿）

## 阶段 0: PoC 验证 ✅

- [x] 0.1 in-process API：`interp::run_outcome` 是公开 API；shared VmContext 经
  `tests/native_pin_e2e.rs` 验证可行
- [x] 0.2 Bencher closure 协议在 z42 端完成，runner 不需 stash/take
- [x] 0.3 PoC 通过现有代码确认，直接进阶段 1

## 阶段 1: 模块拆分 ✅

`src/toolchain/test-runner/src/` 当前布局：

- `main.rs` — CLI 入口 + 调度
- `bootstrap.rs` — in-process VM bootstrap（z42.core eager + lazy declared zpkgs）
- `discover.rs` — TIDX → DiscoveredTest 收集
- `runner.rs` — Setup/Test/Teardown 调度 + exception 分类
- `result.rs` — Outcome 类型
- `format/{mod,tap,json,pretty}.rs` — 输出 formatter
- `exec.rs` — legacy subprocess + 类型名匹配辅助（保留作为 fallback）

## 阶段 2: discover ✅

- [x] 2.1 zpkg 输入支持 → follow-up（.zbc 直跑覆盖所有 dogfood 用例）
- [x] 2.2 `--filter` substring（regex 升级独立 spec）
- [x] 2.3 test_index 在 main.rs in-process 路径就地收集

## 阶段 3: Runner — in-process + Setup/Teardown ✅

- [x] 3.1 `runner::run_one(loaded, test)` 经 `interp::run_outcome` in-process
- [x] 3.2 R2 sink 在 z42 测试体内部自管理；runner 不注入
- [x] 3.3 init_static_fields → Setup → Test → Teardown，teardown 总跑
- [x] 3.4 异常分类：`classify_thrown` 按 `type_desc.name` 后缀 + `type_matches`
  辅助 `[ShouldThrow<E>]` candidate 集
- [x] 3.5 TestCase 参数化展开 → 留 follow-up（spec 显式 v0.1 不做）

## 阶段 4: Bencher → de-scope

z42 端 `Std.Test.Bencher` 类（warmup/samples/median/IQR 都在 z42 中实现）+
[Test] 路径直接调度 [Benchmark] 函数。Runner 不需要独立 bencher.rs。

dogfood 验证（z42.test/tests/dogfood.z42）：5 个 bencher 测试全绿 —— stat
invariants / print_summary / iter counts / black_box passthrough。

进一步打磨（独立 baseline-schema.json 输出、warmup/samples CLI 覆盖）留
follow-up spec。

## 阶段 5: Formatter ✅

`src/toolchain/test-runner/src/format/` 已落地：
- `mod.rs` — Formatter trait
- `tap.rs` — TAP 13
- `json.rs` — 机器可读
- `pretty.rs` — TTY-aware

## 阶段 6-7: scripts + justfile ✅

- [x] 6.1 `scripts/test-changed.sh` 存在 + 行为符合预期
- [x] 7.1 `justfile` 的 `test-changed` / `test-stdlib` recipes 工作

## 阶段 8: 文档 → de-scope（合并到下游归档）

- 设计原理已在 `docs/design/compiler/compiler-architecture.md` 与
  `docs/design/runtime/vm-architecture.md` 描述
- `src/toolchain/test-runner/README.md` 后续 spec 加（不阻塞本归档）

## 阶段 9: 验证 ✅

- [x] `cargo build -p z42-test-runner --release` 通过
- [x] dogfood 26/26 在 test-stdlib.sh 全绿（2026-05-12 修好 cross-zpkg subclass
  catch 之后）
- [x] test-stdlib.sh 6/6 lib 全绿
- [x] TAP / pretty 格式各自工作（test-stdlib.sh 默认 TAP）
- [x] 退出码：通过=0 / 失败=1 / 错误=2
- [ ] JSON 输出 + regex filter + `--bench` 独立路径 → follow-up

## 备注

### 实施依赖

- R1 (test metadata section) ✅
- R2 (z42.test library) ✅
- R4 / R5 无依赖

### 历史"2 known failures"备注修正

S2-S3 实施备注曾写 "dogfood 24/26 pass + 2 已知 failures 在 dogfood 测试设计"。
这 2 个失败（`test_assert_throws_match` / `test_assert_throws_wrong_type_fails`）
实际不是测试设计问题，而是 cross-zpkg subclass walk bug —— `is_subclass_or_eq_td`
只看主 module type_registry，懒加载的 z42.test 类（TestFailure / SkipSignal）
不在主 registry 中，导致 `catch (Exception e)` 不匹配 TestFailure 子类。

2026-05-12 commit `fecac4ed` 修复：subclass walk 添加 `ctx.try_lookup_type`
fallback。当前 dogfood 26/26 全绿。

### Lesson learned

把"测试失败"当作"已知设计 quirk"长期跟踪 → 当时应该停下来查根因。这次
事件触发了 workflow 阶段 8 GREEN 定义加上 `test-stdlib.sh` 入口（之前不在
默认 GREEN 路径里），并新建 `scripts/test-all.sh` 串联所有 stage 防止漏检。

### Follow-up backlog（不在本 spec scope）

- regex `--filter`（独立 spec）
- TestCase 参数化展开（独立 spec）
- `--bench` 独立调度 + baseline-schema.json 兼容输出
- zpkg 输入支持（让 runner 直接接 .zpkg，不必 wrap .zbc）
- `src/toolchain/test-runner/README.md` 用法 + 示例
