# Spec: Non-blocking try-variants for sync primitives

## ADDED Requirements

### Requirement: TrySend non-blocking semantics

#### Scenario: TrySend on unbounded channel always succeeds (unless closed)
- **WHEN** `c = new Channel<long>(); c.TrySend(7)`
- **THEN** returns true; subsequent Recv yields 7

#### Scenario: TrySend on bounded channel returns false when full
- **WHEN** `c = new Channel<long>(2); c.Send(1); c.Send(2); var ok = c.TrySend(3)`
- **THEN** `ok == false`; nothing was queued (Recv recovers 1 then 2 then would block / TryRecv returns empty)

#### Scenario: TrySend on closed channel returns false
- **WHEN** `c.Close(); c.TrySend(...)`
- **THEN** returns false (no exception; mirrors Send behavior of throwing
  vs TrySend silently failing — caller uses bool to react)

### Requirement: TryRead non-blocking semantics

#### Scenario: TryRead succeeds when no writer is active
- **WHEN** `var l = new RwLock<long>(5); var got = l.TryRead((long v) => observed = v)`
- **THEN** `got == true`; body ran with current value

#### Scenario: TryRead returns false when writer holds the lock
- **WHEN** a writer thread is inside `l.Write(...)`'s callback (lock held
  exclusively); main thread calls `l.TryRead(...)`
- **THEN** returns false without blocking; body did NOT run

### Requirement: TryWrite non-blocking semantics

#### Scenario: TryWrite succeeds on uncontended lock
- **WHEN** `var l = new RwLock<long>(0); var got = l.TryWrite((long v) => v + 1)`
- **THEN** `got == true`; subsequent Read observes 1

#### Scenario: TryWrite returns false when readers hold the lock
- **WHEN** N reader threads inside `l.Read(...)` bodies; main thread calls
  `l.TryWrite(...)`
- **THEN** returns false; body did NOT run; stored value unchanged

## MODIFIED Requirements

### Requirement: Channel<T> / RwLock<T> public surface

**Before:**
- `Channel<T>.Send(T)` blocks if bounded+full
- `RwLock<T>.Read(Action<T>)` / `Write(Func<T,T>)` blocks if contended

**After:** (same blocking surface unchanged; ADD non-blocking variants)
- `Channel<T>.TrySend(T) -> bool` — non-blocking send, false if full / closed
- `RwLock<T>.TryRead(Action<T>) -> bool` — non-blocking shared acquire
- `RwLock<T>.TryWrite(Func<T,T>) -> bool` — non-blocking exclusive acquire

## IR Mapping

No new IR. 3 new builtins.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 无变更
- [x] VM interp — 3 个 builtin
