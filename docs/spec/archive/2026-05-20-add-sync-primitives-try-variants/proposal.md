# Proposal: Std.Threading non-blocking try-variants

## Why

`add-sync-primitives` + extensions (bounded channels, rwlock) ship the
**blocking** flavor of every primitive: `Channel.Send` blocks the
producer when the bounded buffer is full; `RwLock.Read` / `Write` block
when contended. For some workflows — polling event loops, opportunistic
work-stealing, latency-sensitive paths — callers want to *probe* whether
the operation can proceed without blocking and fall back to other work
if not.

This spec adds the three obvious `try_*` variants in one batch, since
they share design constraints (non-blocking, return a discriminator or
bool, no waiting on Condvar):

- `Channel<T>.TrySend(T)` — bounded channels only; returns false on full
- `RwLock<T>.TryRead(Action<T>) -> bool` — true if shared lock acquired
- `RwLock<T>.TryWrite(Func<T,T>) -> bool` — true if exclusive lock acquired

## What Changes

- **3 new VM builtins**:
  - `__channel_try_send(slot, v) -> i64` — `0` ok, `1` full (bounded only),
    `2` disconnected. Unbounded channels always return `0` (mpsc::Sender::send
    never blocks)
  - `__rwlock_try_read(slot) -> Value::Array` — `[I64(0), value]` on success
    (caller now holds read lock and must release), `[I64(1)]` on contention
  - `__rwlock_try_write(slot) -> Value::Array` — same shape; held lock vs
    contention
- **z42 facade extensions**:
  - `Channel<T>.TrySend(T v) -> bool` — true on success
  - `RwLock<T>.TryRead(Action<T> body) -> bool` — runs body if acquired
  - `RwLock<T>.TryWrite(Func<T,T> body) -> bool` — runs body if acquired
- **3 stdlib tests** (one per primitive) covering the success + the
  contention/full-return paths

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/sync.rs` | MODIFY | 加 `builtin_channel_try_send` / `builtin_rwlock_try_read` / `builtin_rwlock_try_write`；用 `parking_lot::RwLock::try_read` / `try_write` + `mpsc::SyncSender::try_send` |
| `src/runtime/src/corelib/sync_tests.rs` | MODIFY | 加 6+ 单测：try_send 成功/full/disconnected；try_read/write 成功路径 + 与 outstanding read/write 的冲突路径（同线程持 read 时 try_write 失败；同线程持 write 时 try_read 失败 — 都是 parking_lot 行为）|
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 3 个 builtin（追加末尾） |
| `src/libraries/z42.threading/src/Channel.z42` | MODIFY | 加 `TrySend(T) -> bool` 方法 + `ChannelNative.TrySend(long, object) -> long` [Native] |
| `src/libraries/z42.threading/src/RwLock.z42` | MODIFY | 加 `TryRead(Action<T>) -> bool` / `TryWrite(Func<T,T>) -> bool`；2 个 [Native] |
| `src/libraries/z42.threading/tests/try_variants.z42` | NEW | 3 stdlib tests：try_send 成功 + bounded-full / try_read 成功 / try_write 成功；contention 路径走跨线程 |
| `docs/spec/changes/add-sync-primitives-try-variants/` | NEW | 本 spec |

**只读引用**：

- `src/runtime/src/corelib/sync.rs` 现有 builtin pattern
- parking_lot::RwLock::{try_read, try_write} + mpsc::SyncSender::try_send 文档

## Out of Scope

- **Send/Read/Write with timeout**：`try_send_timeout` / `try_*_for` 变体
  独立 spec `add-sync-primitives-send-timeout`
- **Mutex try_lock**：现有 Mutex<T>.Lock 是 RAII callback，没有暴露 Lock guard
  概念；try_lock 需要重新设计 Lock API。`add-sync-primitives-mutex-try`
  作为 deferred follow-up（若用户场景出现）
- **TryRecv** 已在 add-sync-primitives 里有了，不重复

## Open Questions

- [ ] **TrySend on unbounded channel**：unbounded 总返 0 (success) 还是
      always 0 因为 mpsc::Sender 不会 full？决定：是，unbounded TrySend
      与 Send 等价，永远成功（除非 disconnected）。Design Decision 1
- [ ] **TryRead/TryWrite 返回 bool vs throw**：失败时 throw exception 还是
      返 bool？决定：返 bool，让用户用 if-else 而不是 try-catch。Design
      Decision 2
