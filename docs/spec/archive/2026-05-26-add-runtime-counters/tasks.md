# Tasks: add RuntimeCounters (Phase 1 infrastructure)

> 状态：🟢 已完成 | 创建：2026-05-26 | 完成：2026-05-26 | 模式：minimal-mode（>3 文件，refactor 类）

**变更说明**：introduce `RuntimeCounters` 原子计数器 framework — VmCore 持
`Arc<RuntimeCounters>`，main.rs 接 `--print-stats-on-exit` CLI flag，1 个
demo increment site（builtin call）证明 wiring 正确。Subsystem 全 migrate
是 Phase 2 follow-up 单独 commit。

**原因**：docs/review.md Part 4 D6 last remaining ops/devex P0 item。
production 环境无 runtime metrics（GC pause / JIT compiles / builtin calls /
exceptions thrown）→ 无诊断渠道。CoreCLR EventCounters 等价物。

**文档影响**：`docs/workflow/debugging.md` 加 `--print-stats-on-exit`
section；`docs/review.md` Part 4 D6 状态 → ✅ Phase 1。

## Tasks

- [x] 1.1 `src/runtime/src/counters.rs` NEW — `RuntimeCounters { 6 AtomicU64 }` + Snapshot view + Display impl + **5 unit tests**（含 8-thread concurrent_increments_are_lossless）
- [x] 1.2 `src/runtime/src/lib.rs` MODIFY — `pub mod counters;` 声明
- [x] 1.3 `src/runtime/src/vm_context.rs` MODIFY — `VmCore.counters: Arc<RuntimeCounters>` field + initialize in `new_internal` + `pub fn counters() -> &RuntimeCounters` accessor
- [x] 1.4 `src/runtime/src/corelib/mod.rs` MODIFY — Phase 1 demo increment sites: `exec_builtin_by_id` + `exec_builtin` 顶部 fetch_add(1) on `builtin_calls`
- [x] 1.5 `src/runtime/src/main.rs` MODIFY — `--print-stats-on-exit` CLI flag + Snapshot eprint at end of main（before propagating vm.run Result）
- [x] 1.6 `docs/workflow/debugging.md` — 加 `--print-stats-on-exit` section
- [x] 1.7 `docs/review.md` — Part 4 D6 状态 ⚠️ → ✅ Phase 1（具体 commit hash 待 push 后回填）
- [x] 1.8 Build + tests + commit + push（687/687 lib tests including 5 new counters tests）

## Phase 2 follow-ups（独立 refactor，不在本 spec）

- JIT compile counter — `src/runtime/src/jit/mod.rs::compile_module` 末尾
- Native call counter — `src/runtime/src/interp/exec_native.rs`
- Exception thrown / caught — `src/runtime/src/exception/mod.rs`
- 脚本侧 API: `Std.Diagnostics.RuntimeStats.Snapshot()` 暴露
- Periodic stats emission（`--stats-interval=5s` 定时写 stderr / OTLP exporter）

## Why not full spec process

- Pure additive infrastructure, no wire format change, no opcode change
- 没有 user-visible language behavior change（脚本不能直接访问，仅 CLI + Rust API）
- 5 文件 modify 落在 fix/refactor 最小化模式（per workflow.md 阶段 6.5）
- Phase 2 是更小的独立 refactors，无需大 spec
