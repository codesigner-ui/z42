# Spec: Counter-throttled safepoint fast path

## ADDED Requirements

### Requirement: Fast path skips the Mutex lock N-1 of N calls

#### Scenario: Repeated check_safepoint calls increment internal counter
- **WHEN** `check_safepoint(ctx)` is called 1023 consecutive times with
  no GC request pending
- **THEN** none of those calls take the `gc_phase` Mutex lock; counter
  is decremented to 0 by the 1024th call which then performs the full
  phase / drain check

#### Scenario: GC request observed at next slow-path hit
- **WHEN** worker has 500 iterations remaining on its throttle counter;
  another thread calls `request_gc_pause`; worker continues looping
  `check_safepoint`
- **THEN** worker parks within ≤ 500 additional iterations (the 500
  fast-path hits decrement counter, the 501st hit runs the real check)

### Requirement: Env override honored

#### Scenario: Z42_SAFEPOINT_THROTTLE=1 reverts to per-iter check
- **WHEN** environment variable `Z42_SAFEPOINT_THROTTLE=1` is set when
  the first VmContext is constructed
- **THEN** the throttle counter is initialised to 1, so every
  `check_safepoint` call goes through the slow path (equivalent to
  pre-throttling behaviour for debugging / latency-sensitive testing)

#### Scenario: Z42_SAFEPOINT_THROTTLE not set → default 1024
- **WHEN** no env override is set
- **THEN** throttle = 1024 (Decision 1 default)

## MODIFIED Requirements

### Requirement: check_safepoint hot path cost

**Before:** Every call takes one `parking_lot::Mutex` lock + enum compare
(~10-20 ns) on Idle fast path.

**After:** 1023 of 1024 calls take a single `AtomicU32::load + saturating
decrement + store` (~3-5 ns). Every 1024th call falls through to the
full check (existing logic).

## IR Mapping

No new IR. Optimization is internal to `gc::safepoint::check_safepoint`.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 无变更
- [x] VM interp — `check_safepoint` 改造（interp 调同函数）
- [x] VM JIT — 同（jit_check_safepoint 调同函数）
