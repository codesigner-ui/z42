# Spec: Std.Threading sync primitives (Mutex / Channel)

## ADDED Requirements

### Requirement: Mutex protects exclusive critical section

#### Scenario: single-thread lock + read + write
- **WHEN** `var m = new Mutex<long>(0); m.Lock((v) => v + 1);`
- **THEN** the Func callback runs with the current stored value, its return
  value is written back; subsequent `m.Get()` observes the new value

#### Scenario: two workers concurrently increment counter
- **WHEN** two `Thread.Start(...)` workers each call `m.Lock((v) => v + 1)`
  100 times; main thread joins both
- **THEN** the final value is exactly 200 (no lost updates from data races)

#### Scenario: nested lock on the same Mutex from the same thread deadlocks (documented)
- **WHEN** inside `m.Lock(...)` callback, the same code path calls `m.Lock(...)` again
- **THEN** the worker thread blocks indefinitely (non-reentrant Mutex; v0 behavior)
- **AND** the design.md documents this and recommends restructuring rather than introducing reentrant lock

### Requirement: Channel hands off values between threads

#### Scenario: single send + recv same thread
- **WHEN** `var c = new Channel<long>(); c.Send(42); long v = c.Recv();`
- **THEN** `v == 42`

#### Scenario: producer thread + consumer main thread
- **WHEN** producer thread calls `c.Send(7); c.Send(8); c.Send(9);`; main thread
  calls `c.Recv()` three times
- **THEN** main observes `[7, 8, 9]` in FIFO order

#### Scenario: TryRecv on empty channel
- **WHEN** `c` has no pending values; main calls `c.TryRecv()`
- **THEN** returns discriminator `[I64(1)]` (empty); does not block

#### Scenario: Recv after all senders close throws
- **WHEN** sender drops `c` (all `Send` handles released) and remaining queued
  values are drained; subsequent `Recv()` is called
- **THEN** throws `Std.ChannelDisconnectedException`

## MODIFIED Requirements

### Requirement: Runtime concurrency story (concurrency.md current-state table)

**Before:** `❌ 同步原语 | Mutex<T> / Channel<T> 待 add-sync-primitives`
**After:**  `✅ 同步原语 | Std.Threading.Mutex<T> / Channel<T> 通过 parking_lot + std::sync::mpsc 提供`

## IR Mapping

No new IR instructions. New native fns dispatched via existing `CallNative`
opcode:

| Builtin name | Args → Return | Description |
|---|---|---|
| `__mutex_new` | `(Value initial) → I64 slot_id` | Allocate parking_lot::Mutex<Value> wrapping `initial`; insert into `VmCore.mutexes` |
| `__mutex_lock` | `(I64 slot_id) → Value` | Acquire (blocks), return current stored value (caller transforms + writes back) |
| `__mutex_store` | `(I64 slot_id, Value new) → Null` | Replace stored value (caller must currently hold the lock; checked by storing `holder_thread_id`) |
| `__mutex_unlock` | `(I64 slot_id) → Null` | Release the lock |
| `__channel_new` | `() → I64 slot_id` | Create mpsc channel; insert sender+receiver into VmCore.channels |
| `__channel_send` | `(I64 slot_id, Value v) → Null` | Send v on the channel; throws if disconnected |
| `__channel_recv` | `(I64 slot_id) → Value` | Block until a value arrives; throws ChannelDisconnectedException if all senders closed |
| `__channel_try_recv` | `(I64 slot_id) → Value::Array` | Non-blocking; `[I64(0), Value]` ok / `[I64(1)]` empty / `[I64(2)]` disconnected |
| `__channel_close` | `(I64 slot_id) → Null` | Drop the sender half so consumer's pending Recv returns disconnected |

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：
- [ ] Lexer — 无变更
- [ ] Parser / AST — 无变更
- [ ] TypeChecker — `Mutex<T>` / `Channel<T>` 是常规 generic class；走现有 generic 实例化路径
- [ ] IR Codegen — 无新指令
- [x] VM interp — 新 builtin dispatch；新 VmCore 字段
