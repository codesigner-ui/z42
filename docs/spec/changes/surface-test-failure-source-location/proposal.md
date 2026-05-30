# Proposal: Surface test-failure source location in runner output

## Why

The interp already populates `Std.Exception.StackTrace` with a real
`(file:line[:col])` frame list on every throw
(`src/runtime/src/exception/mod.rs:186-224 populate_stack_trace`), and exposes
`read_stack_trace(value, module)` to retrieve it. But the test runner's
`format_value()` only reads the `Message` field — so when `Assert.Equal(2, 3)`
throws, the user sees:

```
✗ test_arithmetic
    TestFailure: values not equal (expected 3, actual 2)
```

…with no indication of **where** in the test file the assertion fired. The
information exists (z42 runtime computed it ~10 instructions before this
formatter was reached) — it's literally getting thrown away by a one-line
omission in the runner.

This is the #1 friction point when triaging a red test: developer reads
"values not equal", greps the test file for `Assert.Equal(`, finds 6 hits,
runs the suite again with a print added… all because the runner discarded
the line number it already had.

## What Changes

1. **`runner.rs::format_value` → `format_failure_with_stack(val, module)`** —
   read both `Message` AND `StackTrace`. Returns a `FailureDetails` struct
   carrying `message: String` + `stack_trace: Option<String>` separately so
   formatters can render them differently
2. **First user frame extraction** — walk the stack trace string, skip
   frames whose `func_name` starts with `Std.Test.` or contains `.Assert.`
   (framework internals), pull the first remaining frame's `(file:line)` →
   that's the "primary location" shown next to the test name
3. **`TestResult` carries `failure_location: Option<String>` +
   `stack_trace: Option<String>`** — separate from `reason` so JSON output
   stays machine-readable
4. **Pretty formatter** — show `✗ test_foo (test_file.z42:42)` line,
   reason indented underneath, optional `--no-stack` flag to hide full trace
   for terse CI logs
5. **TAP formatter** — YAML diagnostic block gains `location: ...` key and
   `stack: |` literal multi-line for the trace
6. **JSON formatter** — `TestResult` JSON gains `failure_location` and
   `stack_trace` fields (existing `reason` keeps the bare message for
   back-compat parsers)
7. **`runner::classify_thrown` + `classify_should_throw`** — both paths
   that wrap `Outcome::Failed` get upgraded; `format_value` callers in
   Setup/Teardown failure branches also rewired

Out of scope (separate future specs):

- Populating `TestFailure.Location` from the Assert.* call site itself —
  needs compiler `[CallerLineNumber]`-style support (much bigger spec).
  This spec exploits the *already-populated* `StackTrace` field; the
  primary-frame extraction gives the same UX without compiler work
- JIT-mode stack traces (tracked separately — interp-only today)
- Subprocess (`exec.rs`) symmetric upgrade — child process prints
  exception itself; parent captures stderr already. Different code path,
  separate consideration; this spec narrowly targets in-process runner

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/test-runner/src/runner.rs` | MODIFY | `format_value` → `format_failure_with_stack(val, module) -> FailureDetails`；定义 `FailureDetails { message, stack_trace, primary_location }`；提取 `first_user_frame()` helper；更新 `classify_thrown` / `classify_should_throw` / Setup-Teardown failure branches 都构造 `Outcome::Failed { reason }` 时同时填新字段 |
| `src/toolchain/test-runner/src/result.rs` | MODIFY | `Outcome::Failed { reason }` → `Outcome::Failed { reason, location: Option<String>, stack_trace: Option<String> }`；`TestResult` 同步加两字段；JSON `serde::Serialize` impl 自动覆盖 |
| `src/toolchain/test-runner/src/format/pretty.rs` | MODIFY | Failed 输出第一行 `✗ <name> (<location>)` 当 location 存在；reason 缩进；stack 默认展开（terse 模式留给 future flag） |
| `src/toolchain/test-runner/src/format/tap.rs` | MODIFY | YAML diagnostic block 加 `location: ...` 与 `stack: \|` literal blocks |
| `src/toolchain/test-runner/src/format/json.rs` | MODIFY | TestResult JSON 输出多出两 keys |
| `src/toolchain/test-runner/src/runner_tests.rs` | NEW | 单元测试：`format_failure_with_stack` 处理 (a) 无 StackTrace 字段 (b) 有但 null (c) 有完整 trace (d) trace 全是 framework 帧 (e) trace 含混合帧 → primary location 提取正确；用 mock Value::Object 构造 |
| `src/libraries/z42.test/tests/failure_location_demo.z42` | NEW | e2e demo：1 个故意 fail 的 test，验证 runner pretty 输出含 `(failure_location_demo.z42:NN)` 文本（通过 test-stdlib.sh 反向验证 — 但断言失败信息要被人读，不能让 stdlib wave fail。改为 `[ShouldThrow<TestFailure>]` 包裹，断言异常的 `StackTrace` 字段已含 file:line） |
| `docs/design/testing/testing.md` | MODIFY | (用法) § "Failure location in output"：示例 before/after + pretty/TAP/JSON 三种 formatter 输出样本；(设计思路) § "How source location flows"：从 throw 到 user-visible 的完整链路图 + 为什么"primary frame 提取"过滤 framework 帧 |
| `src/libraries/z42.test/README.md` | MODIFY | 能力表加一行 "失败位置展示 ✅ surface-test-failure-source-location" |

**只读引用：**

- `src/runtime/src/exception/mod.rs:139-168 format_stack_trace` — 帧格式 `"  at <func> (<file>:<line>[:<col>])"` 是解析锚点
- `src/runtime/src/exception/mod.rs:226-244 read_stack_trace` — 直接调用即可
- `src/runtime/src/metadata/value.rs` `Value::Object` slot layout — 已熟悉
- `src/libraries/z42.test/src/Failure.z42:24-53 TestFailure` — Location 字段
  存在但保持空（本 spec 不动；未来 `[CallerLineNumber]` spec 再填）

## Out of Scope

- **`TestFailure.Location` 编译期注入** — 需要 z42 引入 `[CallerLineNumber]`
  / `[CallerFilePath]` 类 attribute；分独立 spec
- **JIT 模式 stack trace 填充** — JIT path 尚未实现 `populate_stack_trace`
  调用；本 spec interp-only。JIT 同等支持是 tracked spec
  `2026-05-10-jit-stack-trace` 的工作
- **Subprocess `exec.rs` 路径** — 子进程自己打印异常 + 返回非零退出码；
  父进程 stderr 捕获已经能看到 trace。in-process 路径才是缺位的
- **Caller-site capture for Assert (compiler-side)** — 同 TestFailure.Location，
  需要 attribute infra
- **Diff rendering** (`expected: 2 \n actual: 3` 行对齐) — UX enhancement，
  独立 spec
- **TAP / JSON schema versioning** — pre-1.0 直接加字段，旧 parser 忽略未知
  key（TAP YAML / JSON 都允许）

## Open Questions

- [x] **已裁决**：framework-frame 过滤规则 = 帧 `func_name` startsWith
      "Std.Test." 或 contains ".Assert." → 跳过。若全 stack 都是 framework
      帧（极少；只有 stdlib 自检会出现）→ 返回 None，formatter 不显示
      location 但仍显示 full stack
- [x] **已裁决**：reason 字段保留 backward-compat（只含 message，不含
      location 或 stack）；location / stack 单独成字段。这样老 CI 解析脚本
      继续工作，新 UI 用新字段
- [x] **已裁决**：pretty formatter 默认展开 stack，无 `--no-stack` flag
      v1（terse 留给 future；红测试 = 用户主动想看 detail，默认啰嗦合理）
