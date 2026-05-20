# Design: Std.Threading.RwLock<T>

## Architecture

```
VmCore {
    ..., mutexes, channels, ...,
    rwlocks:        Mutex<HashMap<u64, Arc<parking_lot::RwLock<Value>>>>,   ← NEW
    next_rwlock_id: AtomicU64,                                                ← NEW
}

thread_local! {
    HELD_RWLOCK_GUARDS: RefCell<HashMap<u64, RwLockHeld>>
}

enum RwLockHeld {
    Read(Arc<parking_lot::RwLock<Value>>),
    Write(Arc<parking_lot::RwLock<Value>>),
}
```

`HELD_RWLOCK_GUARDS` mirrors the Mutex pattern (`HELD_MUTEX_GUARDS`) but
adds the Read/Write variant so the release path can decide between
`unlock_shared` and `unlock_exclusive`. The store path requires Write.

## Decisions

### Decision 1: parking_lot::RwLock fairness

**问题**：parking_lot 默认 write-preferring（avoid writer starvation）。
是否暴露 fairness 配置？

**决定**：v0 接受 parking_lot 默认。z42 用户不需要 fine-grained fairness
旋钮；如果出现 starvation，独立 spec 处理（罕见）。

### Decision 2: 同一线程 nested read on different slots

**问题**：thread X 持 slot 1 的 read lock，再 acquire slot 2 的 read lock？

**决定**：完全允许 —— thread-local 表按 slot id 分桶，slot 1 / slot 2
互不影响。同一 slot 不允许重入（parking_lot::RwLock 不是 reentrant；
同线程 second read on same slot 会 deadlock — 由 user code 自检避免）。

### Decision 3: 与 Mutex 同形 RAII callback API

**问题**：`Read(Action<T>)` + `Write(Func<T, T>)` 与 `Mutex<T>.Lock(Func<T,T>)` 一致？

**决定**：**保持一致**。理由：
- 用户记忆负担最小（"lock + callback + auto-release" 模式）
- z42 closure 不能从 callback 返回值（值类型 snapshot 捕获 §4.1）；
  callback 通过 static fields / shared objects 传递结果，与 Mutex 同
- `Read` 用 `Action<T>`（void return）而非 `Func<T, R>`，因为 z42 不支持
  generic methods (no `Read<R>(Func<T, R>)`)
- `Write` 用 `Func<T, T>` 与 Mutex 完全一致

### Decision 4: store 在 read 锁内禁止

**问题**：用户能否在 Read callback 内通过某种 backdoor 改 stored value？

**决定**：**禁止**。`__rwlock_write_store` 严格检查 thread-local 持的是
Write 变体；若持 Read，返回 anyhow Err。z42 facade 中 Read 也根本不调
write_store，所以正常路径下用户不会触发。这是防御性检查，保护 RwLock
不变性。

### Decision 5: parking_lot::RwLock 不 poison

**问题**：与 add-sync-primitives Decision 4 同理。

**决定**：接受 parking_lot 行为（不 poison），与 Mutex 一致。

## Implementation Notes

### Acquire / Release 用 raw RwLock API

parking_lot::RwLock 提供 RawRwLock trait，可绕过 guard 生命周期直接调
`lock_shared` / `unlock_shared` / `lock_exclusive` / `unlock_exclusive`：

```rust
pub fn builtin_rwlock_read_acquire(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__rwlock_read_acquire")?;
    let arc = ctx.core.rwlocks.lock().get(&slot).cloned()
        .ok_or_else(|| anyhow!("__rwlock_read_acquire: unknown slot id {slot}"))?;
    // Acquire shared (blocking).
    let guard = arc.read();
    // Clone the value (read snapshot) then forget the guard so it doesn't
    // unlock at end of scope; we'll unlock manually via force_unlock_read.
    let cloned = (*guard).clone();
    std::mem::forget(guard);
    HELD_RWLOCK_GUARDS.with(|cell| {
        cell.borrow_mut().insert(slot, RwLockHeld::Read(Arc::clone(&arc)));
    });
    Ok(cloned)
}

pub fn builtin_rwlock_read_release(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__rwlock_read_release")?;
    let held = HELD_RWLOCK_GUARDS.with(|cell| cell.borrow_mut().remove(&slot))
        .ok_or_else(|| anyhow!("__rwlock_read_release: slot {slot} not currently locked"))?;
    match held {
        RwLockHeld::Read(arc) => unsafe { arc.force_unlock_read() },
        RwLockHeld::Write(_)  => bail!("__rwlock_read_release: slot {slot} held in write mode"),
    }
    Ok(Value::Null)
}

pub fn builtin_rwlock_write_acquire(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__rwlock_write_acquire")?;
    let arc = ctx.core.rwlocks.lock().get(&slot).cloned()
        .ok_or_else(|| anyhow!("__rwlock_write_acquire: unknown slot id {slot}"))?;
    let guard = arc.write();
    let cloned = (*guard).clone();
    std::mem::forget(guard);
    HELD_RWLOCK_GUARDS.with(|cell| {
        cell.borrow_mut().insert(slot, RwLockHeld::Write(Arc::clone(&arc)));
    });
    Ok(cloned)
}

pub fn builtin_rwlock_write_store(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__rwlock_write_store")?;
    let new_val = args.get(1)
        .ok_or_else(|| anyhow!("__rwlock_write_store: missing new value"))?
        .clone();
    let held = HELD_RWLOCK_GUARDS.with(|cell| cell.borrow().get(&slot).cloned())
        .ok_or_else(|| anyhow!("__rwlock_write_store: slot {slot} not currently locked"))?;
    let arc = match held {
        RwLockHeld::Write(arc) => arc,
        RwLockHeld::Read(_)    => bail!("__rwlock_write_store: slot {slot} held in read mode (cannot store)"),
    };
    unsafe { *arc.data_ptr() = new_val; }
    Ok(Value::Null)
}

pub fn builtin_rwlock_write_release(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__rwlock_write_release")?;
    let held = HELD_RWLOCK_GUARDS.with(|cell| cell.borrow_mut().remove(&slot))
        .ok_or_else(|| anyhow!("__rwlock_write_release: slot {slot} not currently locked"))?;
    match held {
        RwLockHeld::Write(arc) => unsafe { arc.force_unlock_write() },
        RwLockHeld::Read(_)    => bail!("__rwlock_write_release: slot {slot} held in read mode"),
    }
    Ok(Value::Null)
}
```

### `RwLockHeld` needs Clone

`HELD_RWLOCK_GUARDS.with(|cell| cell.borrow().get(&slot).cloned())` clones
to release the borrow. Arc is Clone; the enum derives Clone naturally.

### z42 facade

```z42
public class RwLock<T> {
    private long _slot;

    public RwLock(T initial) {
        this._slot = RwLockNative.New(initial);
    }

    // Multi-reader. body sees a snapshot of the current value; mutations
    // inside body do NOT propagate. Use static fields / shared objects to
    // export observations.
    public void Read(Action<T> body) {
        T snapshot = (T)RwLockNative.ReadAcquire(this._slot);
        body(snapshot);
        RwLockNative.ReadRelease(this._slot);
    }

    // Exclusive writer. body returns the new value to store.
    public void Write(Func<T, T> body) {
        T current = (T)RwLockNative.WriteAcquire(this._slot);
        T next    = body(current);
        RwLockNative.WriteStore(this._slot, next);
        RwLockNative.WriteRelease(this._slot);
    }

    public long SlotId() { return this._slot; }
}

public static class RwLockNative {
    [Native("__rwlock_new")]            public static extern long   New(object initial);
    [Native("__rwlock_read_acquire")]   public static extern object ReadAcquire(long slotId);
    [Native("__rwlock_read_release")]   public static extern object ReadRelease(long slotId);
    [Native("__rwlock_write_acquire")]  public static extern object WriteAcquire(long slotId);
    [Native("__rwlock_write_store")]    public static extern object WriteStore(long slotId, object newValue);
    [Native("__rwlock_write_release")]  public static extern object WriteRelease(long slotId);
}
```

## Testing Strategy

- **Rust unit** (`corelib/sync_tests.rs`):
  - `rwlock_new_returns_monotonic_slot_ids`
  - `rwlock_read_acquire_then_release_no_op`
  - `rwlock_write_store_then_read_observes_new_value`
  - `rwlock_write_store_without_write_acquire_errors`
  - `rwlock_read_release_when_held_write_errors`
  - `rwlock_unknown_slot_errors`
- **z42 stdlib** (`tests/rwlock_basic.z42`):
  - `test_single_thread_read_write_cycle` — write twice + read, assert value
  - `test_concurrent_readers_observe_consistent` — N reader threads see
    the same value; combined sum == N × value
  - `test_writer_blocks_readers` — 1 writer + 2 readers across threads;
    test completes without deadlock; final value reflects write

## Deferred / Future Work

### `add-sync-primitives-rwlock-try`
- Non-blocking `TryRead` / `TryWrite` variants

### `add-sync-primitives-rwlock-upgrade`
- Reader-to-writer upgrade pattern

### `add-sync-primitives-rwlock-fairness`
- Configurable read vs write priority (rare, deferred)
