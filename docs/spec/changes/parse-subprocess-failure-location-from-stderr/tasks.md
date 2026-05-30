# Tasks: parse subprocess failure-location from z42vm stderr

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：fix (test-runner subprocess path)

**变更说明：** Subprocess (`--jobs N>1` / `--legacy-subprocess`) `Outcome::Failed`
currently fills `location: None, stack_trace: None` because the parent
process only has stderr text — no z42 Value to call `read_stack_trace` on.
z42vm already prints the populated stack trace as `\n  at <Func>
(<file:line>)\n…` after the `uncaught exception:` header
(`src/runtime/src/exception/mod.rs::format_uncaught`). Parsing those
trailing lines into a String, then reusing `runner::first_user_frame` for
primary-location extraction, gives subprocess paths the same IDE
jump-to-source as the in-process path.

**原因：** spec-3 `surface-test-failure-source-location` shipped the in-process
half; subprocess gap was tracked as Deferred. Closing it completes the
spec's user-visible value across all 3 execution paths.

**文档影响：** `docs/design/testing/testing.md` Deferred 段移除
`failloc-future-subprocess-stack` 条目；Failure-location 节移除
"Subprocess (`--jobs N>1` 或 `--legacy-subprocess`) 暂不展示 stack" 警告。

## 任务

- [x] 1.1 `src/toolchain/test-runner/src/exec.rs` 新增 `extract_stack_trace_from_stderr(stderr: &str) -> Option<String>`:
  - 找到 `Error: uncaught exception:` 那一行
  - 收集后续连续 `  at ` 开头的行 (允许任意 leading whitespace)
  - 拼回成 multi-line String, `None` if 无 trace 行
- [x] 1.2 `exec::run_one` 的两条 `Outcome::Failed` 路径（TestFailure-arm 和 other-exception-arm）改为:
  - 调 `extract_stack_trace_from_stderr(&stderr)` 拿 stack
  - 调 `crate::runner::first_user_frame(&stack)` 拿 location
  - 填 `Outcome::Failed { reason, location, stack_trace }`
- [x] 1.3 `extract_stack_trace_from_stderr` 单元测试加 4 case:
  - empty stderr → None
  - stderr without uncaught line → None
  - stderr with uncaught + 2 frames → Some 含两行
  - stderr with non-frame lines between → frames仍正确收集 (consecutive only — stop at first non-`at ` line)
- [x] 1.4 `docs/design/testing/testing.md`:
  - Failure-location 节去掉 "Subprocess … 暂不展示 stack" 警告 (改写为 "subprocess + in-process 均支持")
  - Deferred 段删 `failloc-future-subprocess-stack` 行
- [x] 1.5 `cargo test --manifest-path src/toolchain/test-runner/Cargo.toml --release` GREEN
- [x] 1.6 `./scripts/test-all.sh --parallel --jobs=4` 全绿（subprocess wave 必跑）
- [x] 1.7 commit + push
- [x] 1.8 归档 + push 归档

## 备注

- `runner::first_user_frame` 已是 `pub(crate)` (spec-3 ship)，exec.rs 可
  直接 `use crate::runner::first_user_frame;` 复用
- Parser 用 simple line-by-line scan (no regex) — z42vm 自家产 producer，
  format 稳定可控
