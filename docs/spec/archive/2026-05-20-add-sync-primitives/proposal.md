# Proposal: Std.Threading synchronization primitives (Mutex / Channel)

## Why

`add-threading-stdlib` (2026-05-20) ships `Thread.Start(Action) / Join()` but no
way to safely share mutable state between workers. Today the only cross-thread
communication is **shared static fields under data-race risk** —
`thread_shared_static.z42` only works because the two writers run sequentially
(spawn → join → spawn). Real workloads need:

- **Mutex** — exclusive critical section around shared mutable state
- **Channel** — unidirectional MPSC pipe for hand-off without shared state

Without these primitives the concurrency story is incomplete: users either
write racy code or fall back to single-thread sequential execution. C# `lock(){}` /
Rust `Mutex<T>` + `mpsc::channel` are the baseline expectation.

## What Changes

- New VM builtins: `__mutex_new` / `__mutex_lock` / `__mutex_unlock`
  (or RAII-style `__mutex_with_lock`); `__channel_new` / `__channel_send`
  / `__channel_recv` / `__channel_try_recv` / `__channel_close`
- New VmCore slot tables: `mutexes: Mutex<HashMap<u64, parking_lot::Mutex<Value>>>`
  and `channels: Mutex<HashMap<u64, ChannelSlot>>` (same pattern as
  threads / processes); `next_mutex_id` / `next_channel_id` AtomicU64
- New z42 types in `z42.threading`:
  - `Std.Threading.Mutex<T>` — `Lock(Func<T, T>) → void` (RAII via callback)
    or `Lock() → Guard` + `Unlock()` (paired) — to be decided in design
  - `Std.Threading.Channel<T>` — `Send(T) / Recv() → T / TryRecv() → object[]`
  - `Std.Threading.ChannelDisconnectedException` — sender side closed
- 4–6 stdlib tests covering: single-thread lock; concurrent counter; channel
  hand-off; channel close + recv after close

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `mutexes` / `next_mutex_id` / `channels` / `next_channel_id` 4 字段 + 构造路径初始化；`core_arc()` 公开 accessor 供集成测试构造 worker VmContext |
| `src/runtime/src/gc/refs.rs` | MODIFY | **Scope 扩展 2026-05-20**：`GcRef::borrow()` / `borrow_mut()` 从 `try_lock().expect(...)` 改为 `lock()`（blocking）。原 try_lock 是 add-multithreading-foundation Phase 3 从 RefCell 迁移时的过度保守约束（注释明确假设"different-thread access won't happen"），多线程下两个 worker 并发 field_get 同一 GcRef 即 panic，导致 Mutex 的并发测试无法通过。Mutex 本无 reentrant 检测，跨线程阻塞等待是 std Rust 语义。 |
| `src/runtime/src/corelib/sync.rs` | NEW | `__mutex_*` / `__channel_*` native fns 实现 |
| `src/runtime/src/corelib/sync_tests.rs` | NEW | Rust 单测覆盖参数校验 / 跨线程 lock-unlock / channel send-recv |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册新 builtin（追加末尾保留既有 BuiltinId）+ `pub mod sync;` |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | 加 `mutex_protects_concurrent_writes` + `channel_send_recv_across_threads` 集成测试 |
| `src/libraries/z42.threading/src/Mutex.z42` | NEW | `Std.Threading.Mutex<T>` 用户类 + `MutexNative` static class |
| `src/libraries/z42.threading/src/Channel.z42` | NEW | `Std.Threading.Channel<T>` 用户类 + `ChannelNative` static class |
| `src/libraries/z42.threading/src/ChannelDisconnectedException.z42` | NEW | namespace Std；继承 Exception |
| `src/libraries/z42.threading/tests/mutex_basic.z42` | NEW | 单线程 lock + 跨线程 counter |
| `src/libraries/z42.threading/tests/channel_basic.z42` | NEW | 单 send + recv；多 send + 顺序 recv |
| `src/libraries/z42.threading/tests/channel_disconnect.z42` | NEW | Close 后 recv 抛 ChannelDisconnectedException |
| `src/libraries/z42.threading/README.md` | MODIFY | 加 Mutex / Channel 行 |
| `docs/design/runtime/concurrency.md` | MODIFY | "Runtime foundation 现状" 同步原语 ❌ → ✅；next-step spec list 标 `add-sync-primitives` ✅ |
| `docs/design/stdlib/organization.md` | MODIFY | `z42.threading` 行 expand 描述（Mutex + Channel） |
| `docs/design/stdlib/roadmap.md` | MODIFY | `z42.threading.sync` 占位 → "已落地" |
| `docs/design/runtime/vm-architecture.md` | MODIFY | VmCore 字段表加 `mutexes` / `channels` 两行 |
| `docs/spec/changes/add-sync-primitives/` | NEW | proposal / spec / design / tasks（本 spec 自身） |

**只读引用**：

- `src/runtime/src/corelib/process.rs` — slot table 模式参考
- `src/runtime/src/corelib/threading.rs` — Thread builtin 模式参考
- `src/libraries/z42.threading/src/Thread.z42` — z42 facade 模式参考

## Out of Scope

- **`Condvar` / `RwLock` / `Barrier` / `Semaphore`** —— v0 仅 Mutex + Channel；其余原语待后续 spec（与 C# `System.Threading` 完整集对齐是 L3 范围）
- **`async/await` 集成** —— L3 spec `add-spawn-syntax` 范围
- **GC safepoint** —— 独立 spec `add-gc-safepoint`，跟当前 Mutex 串行化策略正交
- **`atomic<T>`** —— Rust `AtomicI64` / `AtomicBool` 等用户类型；独立 spec
- **跨进程 Mutex / Named pipe** —— OS 级 IPC，不在 stdlib v0 范围

## Open Questions

- [ ] **Mutex API 形态**：RAII callback (`Lock(Func)`) vs 显式 pair
      (`Lock() / Unlock()`)。Design.md Decision 1 解决
- [ ] **Channel 容量**：unbounded 默认 vs 必须指定容量。Design.md Decision 2
- [ ] **Channel<T> 跨线程 Send 类型约束**：v0 不静态检查（Value 全部 Send + Sync
      via Arc/Mutex backing）；Design.md Decision 3 记录
- [ ] **lock-while-panic 语义**：parking_lot 不 poison（区别于 std::sync）；
      worker panic 时 Mutex 自动可用。Design.md Decision 4 记录
