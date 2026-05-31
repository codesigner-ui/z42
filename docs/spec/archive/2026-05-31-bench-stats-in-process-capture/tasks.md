# Tasks: in-process stdout capture for bench_stats

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：fix (runtime + test-runner coordination)

**变更说明：** Spec #9 (capture-benchmark-stats-in-testresult) shipped
subprocess-path bench_stats by parsing the user's `Bencher.printSummary`
line out of captured stdout. In-process path (`--jobs 1`, the default)
left `bench_stats` as `None` because the in-process runner shares stdout
with the parent process — there was no per-test capture mechanism.

Close it by reusing the existing `STDOUT_SINKS` thread-local stack in
`z42::corelib::io` (originally built for `TestIO.captureStdout`). The
runner installs a sink around each `[Benchmark]` invocation, pops it
after the test body returns, runs the same `extract_bench_stats_from_stdout`
parser on the captured bytes, then re-emits the bytes to process stdout
so the user still sees the bench output in their terminal (matching
subprocess behavior where the parent captures + propagates stdout).

**原因：** spec #9 explicitly tracked this as Deferred; closing it makes
the default in-process mode behaviorally equivalent to subprocess for
benchmark JSON consumers.

**文档影响：** `docs/design/testing/testing.md` Bencher "Stats in JSON
output" subsection: remove the "in-process limitation" caveat. Deferred
table entry `bench-stats-in-process-capture` removed (closed).

## 任务

- [x] 1.1 `src/runtime/src/corelib/io.rs`:
  - 加 `pub fn push_stdout_sink()` — mirror of `builtin_test_io_install_stdout_sink` for Rust callers (test-runner) without a `VmContext`
  - 加 `pub fn take_stdout_sink() -> Vec<u8>` — pop returns captured bytes
  - Refactor the existing builtin wrappers to delegate (no behavior change for TestIO)
- [x] 1.2 `src/toolchain/test-runner/src/runner.rs::run_one`:
  - 签名: `Outcome` → `(Outcome, Option<BenchStats>)` (mirror `exec::run_one`)
  - Benchmark branch: `push_stdout_sink()` before `exec_test_body`,
    `take_stdout_sink()` after, then re-emit to stdout via `std::io::stdout().write_all` (so terminal still sees output), parse for stats
  - Non-benchmark branch: pass through unchanged (no sink, no stats)
  - All early-return paths return tuple form
- [x] 1.3 `src/toolchain/test-runner/src/main.rs` in-process loop:
  - `runner::run_one` 返回 tuple → chain `.with_bench_stats(stats)` on TestResult
- [x] 1.4 `docs/design/testing/testing.md`:
  - Bencher "Stats in JSON output" 段去除 "In-process 路径不捕获 stdout" 限制描述; 改写为 "全模式 (in-process + subprocess) 均支持"
  - Deferred 表删 `bench-stats-in-process-capture` 行
- [x] 1.5 `cargo build` + `cargo test` GREEN
- [x] 1.6 E2E spot-check: `z42-test-runner bench_demo.zbc --format json` (默认 in-process) 验证 `bench_stats` 出现
- [x] 1.7 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 1.8 commit + push + archive

## 备注

- Re-emitting captured bytes to stdout is essential: without it, in-process
  users would lose the `bench[label] min=...` line in their terminal —
  regression in UX vs pre-spec behavior. Re-emit happens AFTER parse so
  the parser sees the same bytes the user would have seen
- `push_stdout_sink` / `take_stdout_sink` are new pub API on
  `z42::corelib::io`; existing TestIO builtins now delegate to them — no
  behavior change for `TestIO.captureStdout` z42-side API
- 同 thread 内 nested capture stacks still work (TestIO 在 user 的
  benchmark body 里再调 captureStdout — 也会嵌套 push/pop, runner 的
  outer sink 只看到 NOT 被 inner captureStdout 抓走的字节). This matches
  the documented stack semantics
