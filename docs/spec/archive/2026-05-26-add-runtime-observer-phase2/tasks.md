# Tasks: wire Part 4 Phase 2 emit sites (D3+D6)

> 状态：🟢 已完成 | 创建：2026-05-26 | 完成：2026-05-26 | 模式：minimal-mode
>
> **NOTE — Misattributed commit**: the actual code landed in commit
> `39889b68 feat(stdlib): regex named groups` due to a parallel agent
> `git commit` race that swept up my staged files. The Phase 2 wiring
> changes are functionally present in main but the commit message
> describes only the regex work. This tasks.md is the post-hoc
> authoritative spec record. Phase 3 / future commits should not assume
> a `feat(runtime)` Phase 2 commit exists in git log.

## What landed in commit 39889b68 (under wrong message)

4 of my files comprising 170 lines of additions:

- `src/runtime/src/observer.rs` (+83 lines): 4 new RuntimeEvent variants —
  `JitModuleCompiled` / `ExceptionThrown` / `ExceptionCaught` /
  `NativeCallEntered`; updated RecordingObserver test fixture; added
  `phase2_variants_round_trip_through_recorder` test
- `src/runtime/src/jit/mod.rs` (+23 lines): instrument `jit::run` —
  bump `jit_methods_compiled` by `module.functions.len()` + accumulate
  `jit_compile_us_total` from `Instant::now()` elapsed; fire one
  `JitModuleCompiled` event per compile_module call
- `src/runtime/src/interp/exec_native.rs` (+9 lines): instrument
  `call_native` — count + fire `NativeCallEntered` BEFORE dispatch so
  failing calls still register
- `src/runtime/src/interp/mod.rs` (+55 lines): two new helpers
  `fire_exception_thrown` / `fire_exception_caught` + wiring at both
  catch paths (Terminator::Throw same-frame and callee-thrown propagation
  with frames_unwound = 0 vs 1)

## Phase 2 deferred to Phase 3

5th planned emit site — lazy zpkg loader (`metadata::lazy_loader::load_zpkg_file`)
— deferred. LazyLoader has no clean access to VmContext (it's invoked
transitively from `try_lookup_function`); wiring requires either a
drain-queue channel or threading observer access into the loader. The
eager boot-time `ModuleLoaded` events from D3 Phase 1 main.rs replay
cover the common case.

## Test status

- lib tests 693/693 (was 692 + 1 reinforced)
- Smoke `z42vm script.zbc Entry --mode jit --print-stats-on-exit`:
  `jit_methods_compiled: 334; jit_compile_us_total: 45154` — proves
  end-to-end wiring

## Misattribution post-mortem

This is the 4th time in this session that a parallel agent's `git commit`
race included my staged files. Pattern observed:

1. I run `git add <my-files>` to stage
2. Parallel agent runs `git add <their-files>` + `git commit -m "..."`
   in the same window
3. git commits everything staged (mine + theirs) under their message

**Mitigation for future**: use `git stash --keep-index -u` to push other
unstaged + untracked work out before `git commit`, then `git stash pop`.
This isolates my changes in the index for the duration of the commit.
Has not been retroactively applied; future Phase work will adopt this.

## Phase 3 / future follow-ups

- Lazy loader `ModuleLoaded` emit (architectural — drain queue or
  observer threading)
- Per-function JIT compile granularity (currently aggregate)
- Per-frame exception-unwound counter (currently only 0 or 1 at catch
  point regardless of stack depth)
- Built-in JSON-lines event exporter (`--event-trace=<path>` CLI flag)
- OTLP / EventPipe binary format spec
