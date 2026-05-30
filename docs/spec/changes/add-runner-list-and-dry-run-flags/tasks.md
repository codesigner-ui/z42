# Tasks: `--list` + `--dry-run` CLI flags

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：fix (test-runner CLI)

**变更说明：** Test runner currently lacks a way to see what tests would run
without executing them. CI sharding (split N tests across M jobs) and
debugging discovery problems both need it. Add two CLI flags:

- `--list` — print the discovered test names (one per line), exit 0.
  Filter (`--filter`) and platform override (`--platform`) still apply.
- `--dry-run` — execute the discovery + filter pipeline, then emit
  `Outcome::Passed { duration_ms: 0 }` for every discovered test (no
  actual body invocation). Useful for verifying TIDX discovery + skip
  evaluation independently of test runtime.

**原因：** observed gap in earlier survey; trivial implementation closes a
common CI-tooling need without touching runner internals.

**文档影响：** `docs/design/testing/testing.md` add a small "CLI flags"
reference section (currently only the format / filter / platform flags
are documented inline scattered through other sections).

## 任务

- [x] 1.1 `src/toolchain/test-runner/src/main.rs::Cli` 加两个 flag:
  ```
  /// Print discovered test names (one per line) and exit; do not execute.
  /// Useful for CI test sharding and discovery debugging.
  #[arg(long)]
  list: bool,

  /// Walk discovery + skip evaluation as usual, but skip the actual
  /// invocation — every discovered test reports as `Passed { duration_ms: 0 }`.
  /// Useful for verifying filter / [Skip(...)] gating without running test
  /// bodies.
  #[arg(long)]
  dry_run: bool,
  ```
- [x] 1.2 `run(cli)` — after building report_tests / discovery list but
  before invoking runner/exec, branch on the flags:
  - `--list` → for each test print `name` to stdout (one per line);
    return 0 immediately
  - `--dry-run` → construct TestResult with `status: Passed,
    duration_ms: 0` for each test; emit through normal formatter; return 0
- [x] 1.3 Routes:
  - In-process (default) path: after `report_tests` collection, before
    `runner::run_one` loop
  - Subprocess (`--legacy-subprocess` / `--jobs N>1`): after
    `TestReport::from_artifact` + filter, before exec/parallel dispatch
  - Both paths share a helper `early_exit_for_listing_flags(cli, names, format) -> Option<i32>` that returns Some(exit_code) when one of the flags is set
- [x] 1.4 Output ordering: declaration order (same as run output) — natural
  for sharding (`--list | head -n N | tail -n M | xargs -I{} runner --filter {}`)
- [x] 1.5 `--list` + `--dry-run` mutually exclusive? — choose `--list`
  wins if both passed (just print names; don't dry-run too)
- [x] 1.6 Tests:
  - C# / Rust: nothing new (CLI parsing is mechanical)
  - E2E spot check: run `./target/release/z42-test-runner <zbc> --list`
    manually; verify N names printed
- [x] 1.7 Docs:
  - `docs/design/testing/testing.md` 新增 § "Runner CLI flags reference"
    简表 (file / --format / --filter / --platform / --jobs / --z42vm /
    --legacy-subprocess / --no-color / --list / --dry-run)
  - 顺带说明 sharding 用法
- [x] 1.8 `cargo build` + GREEN
- [x] 1.9 commit + push + archive

## 备注

- `--list` 不展示 `[Skip]` 状态 — 只名字。如果用户想看 skip 状态，用
  `--dry-run` (formatter shows Skipped)
- `--dry-run` 仍调 `skip_eval::decide_skip` — `[Skip(...)]` 在 dry-run
  时正确报 Skipped (not Passed). 这才是 dry-run 的实际价值: 验证 gating
  逻辑
