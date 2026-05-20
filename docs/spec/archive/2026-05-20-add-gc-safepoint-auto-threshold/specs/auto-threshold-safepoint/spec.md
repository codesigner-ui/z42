# Spec: Safepoint-aware auto-threshold GC trigger

## ADDED Requirements

### Requirement: Auto-threshold pressure defers collect to next safepoint

#### Scenario: Allocation under pressure sets the flag instead of collecting inline
- **WHEN** `ArcMagrGC::alloc_array` is called and `maybe_auto_collect`'s
  pressure conditions trip
- **THEN** instead of calling `self.collect_cycles()` synchronously, the
  heap stores `true` into the external `needs_auto_collect` AtomicBool
  (no mark+sweep yet)
- **AND** the calling `alloc_array` returns normally with the just-allocated Value

#### Scenario: Safepoint drains the flag and runs collect
- **WHEN** `check_safepoint(ctx)` is called and the phase is Idle but
  `ctx.core.needs_auto_collect.swap(false, AcqRel)` returns `true`
- **THEN** the calling thread runs `let _g = request_gc_pause(ctx); ctx.heap().collect_cycles()` (taking the stop-the-world guard, doing the collect, releasing on drop)

#### Scenario: Concurrent workers with tight heap budget no race
- **WHEN** 4 worker threads loop allocating arrays; `max_bytes` is small
  enough that auto-threshold trips dozens of times during the run
- **THEN** the test completes without panic, without deadlock, and the
  GC scanner never sees a half-written reg (no UAF / no segfault under
  TSAN)

## MODIFIED Requirements

### Requirement: Concurrent GC story (concurrency.md)

**Before:**
`🟡 并发 GC | safepoint 协议落地（add-gc-safepoint）；... auto-threshold 内部路径仍 unguarded`

**After:**
`✅ 并发 GC (safepoint complete) | script-explicit + auto-threshold 都通过 safepoint 协议串行化（add-gc-safepoint + add-gc-safepoint-auto-threshold）。mark+sweep 本身仍单线程 — 并行化是 add-concurrent-gc / Phase A 性能轨道范围`

## IR Mapping

No new IR. Behavior change is internal to the runtime.

## Pipeline Steps

- [ ] Lexer — 无变更
- [ ] Parser / AST — 无变更
- [ ] TypeChecker — 无变更
- [ ] IR Codegen — 无变更
- [x] VM interp — `check_safepoint` 扩展（多一个 flag 检查 + drain 路径）
- [ ] VM JIT — out of scope（`add-gc-safepoint-jit`）
