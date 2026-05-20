# Spec: GC safepoint protocol (interp-only v0)

## ADDED Requirements

### Requirement: Mutators park at safepoint when GC requests pause

#### Scenario: GC request seen at backward branch
- **WHEN** worker thread is in a `while` loop and another thread calls
  `collect_cycles` (which sets `gc_phase = Requested`)
- **THEN** the worker observes `gc_phase = Requested` at the next backward
  branch's safepoint check, increments `parked_count`, and waits on the
  Condvar until `gc_phase` returns to `Idle`

#### Scenario: GC request seen at function entry
- **WHEN** a spawned worker calls `exec_function` while GC is in the
  `Requested` or `Marking` phase
- **THEN** the worker parks at the function-entry safepoint check before
  executing any instruction, contributing to `parked_count`

#### Scenario: GC waits for all other VmContexts to park before marking
- **WHEN** thread A invokes `collect_cycles`, the runtime has 3 other
  VmContexts (A's own + 3 workers)
- **THEN** A waits on the Condvar until `parked_count == 3` (all *other*
  contexts) before transitioning to `Marking`

### Requirement: Mark phase observes consistent root state

#### Scenario: 4 concurrent mutators allocating + 1 GC collecting, no race
- **WHEN** 4 worker threads each loop allocating arrays and writing to local
  registers; a 5th thread loops `collect_cycles` for 100 iterations
- **THEN** the test completes without panic, without Rust data-race detector
  (when applicable), and without observable corruption (all allocated arrays
  still readable, GC stats consistent)

#### Scenario: Mutators resume after collect completes
- **WHEN** the GC thread finishes mark + sweep and sets `gc_phase = Idle`
- **THEN** all parked mutators wake (notify_all) and resume their next
  instruction within bounded latency

## MODIFIED Requirements

### Requirement: Concurrent GC story (concurrency.md current-state table)

**Before:**
`❌ 并发 GC | mark-sweep 仍单线程；safepoint 协议待 add-gc-safepoint`

**After:**
`🟡 并发 GC | safepoint 协议落地（add-gc-safepoint 2026-05-20）；mutators 在 backward branch / Call return / function entry 处 park，GC mark+sweep stop-the-world 跑。Mark/sweep 本身仍单线程，并发化待 add-concurrent-gc`

## IR Mapping

No new IR instructions. Safepoint check is a Rust-side function call
threaded through the interp dispatch loop — invisible at the IR level.

## Pipeline Steps

- [ ] Lexer — 无变更
- [ ] Parser / AST — 无变更
- [ ] TypeChecker — 无变更
- [ ] IR Codegen — 无变更
- [x] VM interp — safepoint check 插在 dispatch 循环 + function entry
- [ ] VM JIT — out of scope（v0 interp-only；JIT 走独立 `add-gc-safepoint-jit`）
