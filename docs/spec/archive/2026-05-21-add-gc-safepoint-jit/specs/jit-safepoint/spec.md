# Spec: JIT-mode GC safepoint insertion

## ADDED Requirements

### Requirement: JIT-compiled code parks at safepoint when GC requests pause

#### Scenario: JIT worker parks at backward branch
- **WHEN** worker thread executing a JIT-compiled function with a `while`
  loop; another thread calls `collect_cycles` (sets `gc_phase = Requested`)
- **THEN** the worker observes `gc_phase = Requested` at the next backward
  branch's `jit_check_safepoint` call, increments `parked_count`, and
  waits on the Condvar until phase returns to Idle

#### Scenario: JIT worker parks at function entry
- **WHEN** a JIT-compiled function is invoked (via Call / CallIndirect)
  while GC is in `Requested` or `Marking` phase
- **THEN** the JIT function parks at its entry safepoint check before
  executing any instruction

#### Scenario: JIT worker parks after returning from callee
- **WHEN** a JIT function calls a long-running callee that completes
  while GC requests a pause partway through
- **THEN** the JIT function's post-Call safepoint check parks before
  the next instruction

### Requirement: JIT mode multi-thread completes without deadlock

#### Scenario: 4 JIT workers + 1 GC collector run 100 rounds, no deadlock
- **WHEN** 4 worker threads run JIT-compiled allocation loops in parallel;
  a 5th thread loops `request_gc_pause` + `collect_cycles` 100 times
- **THEN** the test completes within bounded time, all workers join
  successfully, gc_cycles >= 100

## MODIFIED Requirements

### Requirement: Safepoint coverage matrix (vm-architecture.md)

**Before:** "JIT-mode safepoint | 不实施；JIT native code 无 Rust 插桩点 | add-gc-safepoint-jit"

**After:** "JIT-mode safepoint | ✅ 已落地（add-gc-safepoint-jit 2026-05-21）：JIT translate 在 function entry / backward Br / BrCond / Call 返回后 emit `jit_check_safepoint` helper call，与 interp 同 4 个 site 完全对齐 | —"

## IR Mapping

No new IR. Adds one JIT helper (`jit_check_safepoint`) which calls the
shared `crate::gc::safepoint::check_safepoint` from compiled code.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 无变更
- [x] VM interp — 无变更（add-gc-safepoint 已完成）
- [x] VM JIT — 新 helper + translate.rs 4 处插桩
