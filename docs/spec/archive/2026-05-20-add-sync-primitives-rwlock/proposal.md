# Proposal: Std.Threading.RwLock<T> — multi-reader / single-writer

## Why

`add-sync-primitives` (2026-05-20) ships `Std.Threading.Mutex<T>` which
enforces exclusive access. Many real workloads have a **read-heavy
contention pattern** (config caches, lookup tables, sliding-window stats)
where dozens of readers run for every writer. Mutex<T> serialises all
readers needlessly.

`std::sync::RwLock` (Rust) and `System.Threading.ReaderWriterLockSlim` (C#)
both exist for exactly this reason: multiple concurrent readers may hold
a shared lock as long as no writer is active; a single writer blocks all
readers and other writers. v0 lands the parking_lot-backed version with
the same RAII callback API shape as Mutex<T> for consistency.

## What Changes

- **New `Std.Threading.RwLock<T>` class** with two callback methods:
  - `Read(Action<T> body)` — acquire shared lock, invoke body with a
    snapshot of the current value, release. Multiple readers may run
    concurrently
  - `Write(Func<T, T> body)` — acquire exclusive lock, invoke body with
    the current value, store its return value, release. Excludes all
    readers and other writers
- **6 new VM builtins** keyed off `VmCore.rwlocks` slot table mirroring
  the Mutex pattern: `__rwlock_new` / `__rwlock_read_acquire` /
  `__rwlock_read_release` / `__rwlock_write_acquire` / `__rwlock_write_store`
  / `__rwlock_write_release`
- **thread-local held-guard parking** extended to track *whether* the
  current acquire is shared (read) or exclusive (write), so release picks
  the correct unlock path
- **3 stdlib tests** covering single-thread read+write cycle, concurrent
  readers, and concurrent reader+writer back-pressure

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `rwlocks: Mutex<HashMap<u64, Arc<parking_lot::RwLock<Value>>>>` + `next_rwlock_id: AtomicU64`，构造路径初始化 |
| `src/runtime/src/corelib/sync.rs` | MODIFY | 加 `RwLockHeld` enum（Read/Write 区分释放路径）+ thread-local `HELD_RWLOCK_GUARDS` map；6 个 builtin (`builtin_rwlock_new` / `builtin_rwlock_read_acquire` / `builtin_rwlock_read_release` / `builtin_rwlock_write_acquire` / `builtin_rwlock_write_store` / `builtin_rwlock_write_release`) |
| `src/runtime/src/corelib/sync_tests.rs` | MODIFY | 加 6+ 单测：参数校验 / 单线程读写循环 / write 后 read 看到新值 / read 不允许 store / 跨 Acquire 类型不匹配错误 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 6 个 builtin（追加末尾保留既有 BuiltinId） |
| `src/libraries/z42.threading/src/RwLock.z42` | NEW | `Std.Threading.RwLock<T>` + `RwLockNative` static class |
| `src/libraries/z42.threading/README.md` | MODIFY | 加 RwLock 行 |
| `src/libraries/z42.threading/tests/rwlock_basic.z42` | NEW | 3 stdlib tests：单线程 read+write / 跨线程多 reader 并发 / write blocks while readers active |
| `docs/design/stdlib/organization.md` | MODIFY | `z42.threading` 行短句补 RwLock |
| `docs/design/runtime/vm-architecture.md` | MODIFY | VmCore 字段表加 `rwlocks` / `next_rwlock_id` 两行 |
| `docs/spec/changes/add-sync-primitives-rwlock/` | NEW | 本 spec |

**只读引用**：

- `src/runtime/src/corelib/sync.rs` 现有 Mutex builtins（模式参考）
- `docs/spec/archive/2026-05-20-add-sync-primitives/design.md`（Mutex callback API 设计）
- parking_lot::RwLock + RawRwLock 文档

## Out of Scope

- **Reader-to-writer upgrade**：`std::sync::RwLock` 不直接支持；通常通过
  drop-read + acquire-write + re-check 实现。v0 不暴露
- **TryRead / TryWrite**：非阻塞 try 变体，独立 spec `add-sync-primitives-rwlock-try`
- **Read 返回 T**：`Read(Action<T>)` 不让 body 返回值（z42 不支持泛型方法
  `Read<R>(Func<T, R>)`）；用户需要通过 static field / shared object 外化结果，
  同 Mutex 内 callback 处理共享可变状态的约定

## Open Questions

- [ ] **fairness**：parking_lot::RwLock 默认 "write-preferring"（避免 writer
      饥饿）但不 strict FIFO；v0 接受默认。Design Decision 1
- [ ] **同一线程重入两个 RwLock slot 怎么办**：thread-local map 按 slot id
      分桶，不同 slot 互不影响。Design Decision 2
