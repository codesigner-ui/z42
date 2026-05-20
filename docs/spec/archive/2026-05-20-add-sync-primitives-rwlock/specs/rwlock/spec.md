# Spec: Std.Threading.RwLock<T>

## ADDED Requirements

### Requirement: Single-thread read/write cycle

#### Scenario: Read after Write observes the written value
- **WHEN** `var l = new RwLock<long>(0); l.Write((long v) => v + 10); l.Read((long v) => observed = v);`
- **THEN** `observed == 10` (after the Write callback returns 10, that
  becomes the new stored value; subsequent Read sees 10)

#### Scenario: Write after Read returns to a fresh snapshot
- **WHEN** the user does `l.Read(... no-op ...)` then `l.Write((long v) => v + 5)`
- **THEN** the stored value increments by 5 (Read is non-mutating)

### Requirement: Concurrent readers share the lock

#### Scenario: Multiple Read callers run concurrently
- **WHEN** N threads each enter `l.Read(body)` where body does some work
  and observes the value
- **THEN** they all hold the read lock simultaneously without serialising;
  end-to-end wall time is roughly max(body_time) not sum(body_time)
- **AND** any Write call interleaved must wait for all readers to release

### Requirement: Writer excludes readers

#### Scenario: Read blocks while Write is active
- **WHEN** thread A is mid-Write callback (holds exclusive lock); thread B
  calls Read on the same RwLock
- **THEN** B blocks until A's Write callback returns and `__rwlock_write_release`
  fires, then B proceeds with the post-write value

### Requirement: Release path matches acquire path

#### Scenario: Calling write_store while holding read errors
- **WHEN** thread holds a read lock and calls `__rwlock_write_store`
- **THEN** the builtin returns Err ("attempting to store via read-acquired slot")

#### Scenario: Calling release without a held lock errors
- **WHEN** thread calls `__rwlock_read_release` or `__rwlock_write_release`
  without holding the corresponding slot
- **THEN** the builtin returns Err ("slot not currently locked on this thread")

## MODIFIED Requirements

### Requirement: Std.Threading package surface (organization.md)

**Before:** `Thread / ThreadException + Mutex<T> / Channel<T> (unbounded + new Channel<T>(N) bounded ...) / ChannelDisconnectedException`

**After:** `Thread / ThreadException + Mutex<T> / RwLock<T> (multi-reader / single-writer) / Channel<T> (unbounded + bounded) / ChannelDisconnectedException`

## IR Mapping

No new IR. 6 new builtins.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 无变更
- [x] VM interp — 6 个 builtin + VmCore 字段
