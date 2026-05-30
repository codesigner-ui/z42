# Tasks: Surface test-failure source location in runner output

> 状态：🟢 已完成 | 完成：2026-05-30 | 创建：2026-05-30 | 类型：vm (test runner)

## 进度概览

- [x] 阶段 1: `result.rs` — Outcome::Failed 三字段化 + TestResult 同步
- [x] 阶段 2: `runner.rs` — `format_failure_with_stack` + `first_user_frame`
- [x] 阶段 3: 三 formatter 升级（pretty / tap / json）
- [x] 阶段 4: 单元测试 (`runner_tests.rs` 8-case 矩阵 + parser smoke)
- [x] 阶段 5: E2E 演示 (`failure_location_demo.z42` — 验 runtime 仍填 stack)
- [x] 阶段 6: 文档（用法 + 设计思路）
- [x] 阶段 7: GREEN + commit + archive

## 阶段 1: `result.rs`

- [x] 1.1 `Outcome::Failed` 加 `location: Option<String>` + `stack_trace: Option<String>` 字段
- [x] 1.2 `TestResult` 加同名两字段 + `#[serde(skip_serializing_if = "Option::is_none")]`
- [x] 1.3 `TestResult::from_outcome` 适配新 enum 形态；Passed/Skipped 显式
  传 None
- [x] 1.4 `result.rs::tests::sample_results` 顺带 demo 一个含 location 的
  failed sample（不影响现有 summary_aggregates_correctly 断言）

## 阶段 2: `runner.rs`

- [x] 2.1 NEW `pub(crate) struct FailureDetails { message, primary_location, stack_trace }`
- [x] 2.2 NEW `pub(crate) fn format_failure_with_stack(val: &Value, module: &Module) -> FailureDetails`
  - Pull Message field（保留现 format_value 逻辑）
  - 调 `z42::exception::read_stack_trace(val, module)` 拿 stack
  - 调 `first_user_frame(&stack)` 拿 primary
- [x] 2.3 NEW `fn first_user_frame(stack: &str) -> Option<String>`
  - 行扫描 `  at <func> (<file>:<line>[:<col>])`
  - 跳过 framework 帧（`func` startsWith `"Std.Test."` 或 contains `".Assert."`）
  - 返回第一条 user 帧的 `<file>:<line>` 串（不含 col 也不带 col；让 col 仅出现在 full stack）
  - 无 user 帧 / 全部无 `(file:line)` → None
- [x] 2.4 NEW `fn is_framework_frame(func_name: &str) -> bool` — Decision 2 的纯函数实现
- [x] 2.5 删除旧 `format_value(val: &Value) -> String`（现 inline 在 classify_*
  里）；改 `classify_thrown(val: &Value, module: &Module) -> Outcome`，需要传 module
- [x] 2.6 `classify_thrown` 拆解：先调 `format_failure_with_stack`，按 SkipSignal /
  TestFailure / 其他 routes 到对应 Outcome，Failed 路径填 location +
  stack_trace
- [x] 2.7 `classify_should_throw` 同理：mismatch 路径用 new helper（虽然
  expected throw 一般来自 runtime/JIT 也会有 stack）
- [x] 2.8 Setup/Teardown failure 路径：调 `exec_one`/`exec_named` 处包装
  Outcome::Failed 时填 `location: None, stack_trace: None`（这条路径 reason
  是 Rust 端构造的字符串如 "Setup: VM error: …"，没 z42 exception value）
- [x] 2.9 `exec_test_body` 的 `Err(e)` 路径同上 — Rust-side error，无 stack

## 阶段 3: 三 formatter 升级

- [x] 3.1 `pretty.rs::print` Failed arm:
  - 第一行 `✗ <name>  (<location>)` 当 location 存在；否则 `✗ <name>`
  - reason 缩进 4 spaces（保持现有红色）
  - stack 存在时输出 `      stack:` 引导 + 每帧缩进 8 spaces + dim 色
- [x] 3.2 `tap.rs::print` Failed arm:
  - 现有 `message: 'reason'` 保留（backward compat）
  - location 存在 → 加 `  location: '<loc>'`
  - stack 存在 → 加 `  stack: |\n    <line1>\n    <line2>...` 用 literal block
  - 新 helper `format_yaml_literal_block(content, indent) -> String` 处理缩进
- [x] 3.3 `json.rs::print` — TestResult serde 已自动序列化新字段，但
  inspect 输出格式确认 ordering 不变（name → status → duration_ms → reason →
  failure_location → stack_trace）；如需手动 ordering，加 `#[serde(rename = "…")]`
  与 field 顺序保持一致
- [x] 3.4 三个 formatter 文件内的 existing unit tests（`tap_format_matches_v13_skeleton`
  / `json_serialization_round_trip` / `json_passed_omits_reason_field`）确认
  仍通过 — 新字段默认 None 时输出与旧 byte-equivalent

## 阶段 4: 单元测试

- [x] 4.1 NEW `src/toolchain/test-runner/src/runner_tests.rs`：
  Design Testing Strategy 表 case 1-8 + first_user_frame 独立测试 4 case
  - Mock helper：用 `z42::metadata::{Module, TypeDesc, Value}` 构造 minimal
    Exception subclass with Message + (optional) StackTrace fields；不需要
    跑完整 VM
- [x] 4.2 `runner.rs` 末尾 `#[cfg(test)] mod runner_tests;`
- [x] 4.3 NEW pretty / tap formatter 测试：用 hand-crafted
  TestResult{failure_location, stack_trace} 验证输出形状（不只 reason）
- [x] 4.4 `cargo test --manifest-path src/toolchain/test-runner/Cargo.toml`
  局部 GREEN

## 阶段 5: E2E 演示

- [x] 5.1 NEW `src/libraries/z42.test/tests/failure_location_demo.z42`：
  - `[Test] void test_stack_trace_populated_by_runtime()`:
    try { `Assert.Equal(2, 3)` }
    catch (TestFailure e) {
      `Assert.True(e.StackTrace.Contains("Assert.z42"))`;
      `Assert.True(e.StackTrace.Contains("failure_location_demo.z42"))`;
    }
  - 验证：runtime 的 populate_stack_trace 仍在跑 + 帧内容包含 z42-side
    file 名（不只是 Rust internal）
- [x] 5.2 `./scripts/test-stdlib.sh z42.test` GREEN

## 阶段 6: 文档

### 6.A 用法（user-facing）

- [x] 6.A.1 `docs/design/testing/testing.md` 新增 § "Failure location in
  runner output"：
  - Before/after 输出样本（pretty / TAP / JSON 三种 formatter 都各列一段）
  - 解释 `failure_location` 来自 stack trace 的 first non-framework frame
  - CI 集成 hint：JSON `failure_location` 字段可被 IDE / CI 工具消费做
    jump-to-source
- [x] 6.A.2 `src/libraries/z42.test/README.md` 能力表加：
  `失败位置展示 ✅ surface-test-failure-source-location | runner pretty/TAP/JSON 都自动包含 file:line`

### 6.B 设计思路（design rationale）

- [x] 6.B.1 `docs/design/testing/testing.md` 同节后续段 § "How source location
  flows from throw to UI"：
  - 完整链路图（interp populate → runtime read → runner extract → formatter render）
  - 解释 Decision 2 (framework frame filter 算法 + 为什么不用 regex)、
    Decision 3 (full stack 不过滤的理由)、Decision 4 (pretty 排版)、
    Decision 6 (JSON field 命名为 `failure_location` 而非 `location`)
  - 引用 `docs/spec/archive/2026-05-30-surface-test-failure-source-location/design.md`

## 阶段 7: GREEN + commit + archive

- [x] 7.1 `cargo build --manifest-path src/toolchain/test-runner/Cargo.toml --release` 通过
- [x] 7.2 `cargo test --manifest-path src/toolchain/test-runner/Cargo.toml` GREEN
- [x] 7.3 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 7.4 commit + push（spec + 实现 + 测试 + 文档 + demo）
- [x] 7.5 归档 `docs/spec/changes/surface-test-failure-source-location/` →
  `docs/spec/archive/2026-05-30-surface-test-failure-source-location/`
- [x] 7.6 push 归档 commit

## 备注

- 0 compiler / runtime 改动；纯 test-runner 端 + format 文档
- `read_stack_trace` 已存在 `src/runtime/src/exception/mod.rs:228`，导出
  签名形如 `pub fn read_stack_trace(value: &Value, module: &Module) -> Option<String>`，
  直接 use
- Outcome::Failed 三字段化是 Rust enum 变体扩展，所有调用点 match 出 reason
  的会编译失败（match must be exhaustive） — 这是 catch-net：漏改一处
  cargo build 即报错
