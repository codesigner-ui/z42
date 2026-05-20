# Design: Non-blocking try-variants

## Architecture

```
__channel_try_send(slot, v) → i64 discriminator
   ChannelSender::Unbounded(tx) → tx.send(v) (never blocks) → 0 ok / 2 disconnected
   ChannelSender::Bounded(tx)   → tx.try_send(v) → match TrySendError:
                                   None         → 0 ok
                                   Full(v)      → 1 full
                                   Disconnected → 2 disconnected

__rwlock_try_read(slot) → Value::Array
   arc.try_read() → Some(guard) → forget + thread-local Read; return [I64(0), cloned_value]
                  → None        → [I64(1)] (contention)

__rwlock_try_write(slot) → Value::Array
   arc.try_write() → Some(guard) → forget + thread-local Write; return [I64(0), cloned_value]
                   → None        → [I64(1)] (contention)
```

## Decisions

### Decision 1: TrySend on unbounded always succeeds

**问题**：unbounded channel 的 `mpsc::Sender::send` 永不阻塞（除 disconnected
外）。TrySend 在 unbounded 上的行为？

**决定**：TrySend 等价 Send（成功或 disconnected，不会 Full）。z42 facade
TrySend 返 true（success / non-blocking 都算 true）；returned false 仅在
disconnected 时。bounded channels 的 Full 路径返 false。

### Decision 2: TryRead/TryWrite 失败返 bool

**问题**：用户代码风格：`if (l.TryWrite(...)) { ... }` vs
`try { l.TryWrite(...); } catch { ... }`？

**决定**：返 bool。理由：
- 失败不是 "异常"（非阻塞是预期；调用方主动选择）
- bool 让用户写 if-else 而非 try-catch
- 与 .NET / Rust 同名 API 习惯一致 (`TryGetValue` / `try_write`)

### Decision 3: Mutex.TryLock 不在本 spec

**问题**：为什么不顺手加 Mutex.TryLock？

**决定**：Mutex 现有 API 是 `Lock(Func<T,T>)` RAII callback，没有暴露 lock
guard 概念给用户。TryLock 需要重新设计 — 要么返回 Optional<Guard>（生命
周期复杂）要么返回 bool + 失败时不执行 callback（与 RwLock.TryRead/TryWrite
对齐，但 Mutex callback 已是 RAII 风格，加一个 `TryLock(Func) -> bool` 是
合理的）。

实际权衡：要不要加？v0 不加 — 用户极少需要"if Mutex 没人占着才做"，更常是
"等到没人占着"。如有用户场景出现，独立 spec `add-sync-primitives-mutex-try`
处理。

### Decision 4: parking_lot::RwLock::try_read 行为

**问题**：parking_lot::RwLock::try_read 在同线程已持 read lock 时 行为？

**决定**：parking_lot::RwLock 是 write-preferring 但 reader-reentrancy 友好；
同线程已持 read lock 时 try_read 通常成功（增 shared count）。但我们的
thread-local map 按 slot id 单 entry — 重复 try_read 同 slot 在同线程会
覆盖 map 中的 entry，下次 try_read_release 只释放一次。这是潜在 leak。

**v0 简化**：thread-local map 检查若 slot 已存在 → 返 [I64(1)] (contention)
而非允许重入。z42 facade 用户场景下不会嵌套 TryRead 同一 slot — 直接拦截
而非允许 reentrancy 是更安全的默认。

实现：try_read 入口先看 HELD_RWLOCK_GUARDS 中 slot 是否存在；存在 → 返 [I64(1)]。
仅当不存在时才尝试 try_read。

### Decision 5: __channel_try_send 不消耗 Value on Full

**问题**：bounded `SyncSender::try_send(v)` 在 Full 时返 `Err(TrySendError::Full(v))`
把 v 还回来。z42 facade 怎么处理？

**决定**：v0 不还回。z42 调用方传 v 进 TrySend；失败时 v 进入 GC（如果是
heap object，引用计数减一并最终回收）；用户记得 v 在失败时不再可用。
未来若要 caller 检查"哪些 sends 失败了重传"，独立 spec 加返回 v 的版本。

## Implementation Notes

```rust
// __channel_try_send
pub fn builtin_channel_try_send(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__channel_try_send")?;
    let val  = args.get(1)
        .ok_or_else(|| anyhow!("__channel_try_send: missing value argument"))?
        .clone();

    enum SendHandle {
        Unbounded(mpsc::Sender<Value>),
        Bounded(mpsc::SyncSender<Value>),
    }
    let handle: SendHandle = {
        let map = ctx.core.channels.lock();
        let chan = map.get(&slot)
            .ok_or_else(|| anyhow!("__channel_try_send: unknown slot id {slot}"))?;
        let sender = match chan.sender.as_ref() {
            Some(s) => s,
            None    => return Ok(Value::I64(TRY_SEND_DISCONNECTED)),
        };
        match sender {
            ChannelSender::Unbounded(tx) => SendHandle::Unbounded(tx.clone()),
            ChannelSender::Bounded(tx)   => SendHandle::Bounded(tx.clone()),
        }
    };
    let kind = match handle {
        SendHandle::Unbounded(tx) => match tx.send(val) {
            Ok(()) => TRY_SEND_OK,
            Err(_) => TRY_SEND_DISCONNECTED,
        },
        SendHandle::Bounded(tx) => match tx.try_send(val) {
            Ok(()) => TRY_SEND_OK,
            Err(mpsc::TrySendError::Full(_))         => TRY_SEND_FULL,
            Err(mpsc::TrySendError::Disconnected(_)) => TRY_SEND_DISCONNECTED,
        },
    };
    Ok(Value::I64(kind))
}

const TRY_SEND_OK:           i64 = 0;
const TRY_SEND_FULL:         i64 = 1;
const TRY_SEND_DISCONNECTED: i64 = 2;
```

```rust
// __rwlock_try_read
pub fn builtin_rwlock_try_read(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__rwlock_try_read")?;
    // Reject same-thread same-slot reentrancy (Decision 4).
    let already_held = HELD_RWLOCK_GUARDS.with(|cell| cell.borrow().contains_key(&slot));
    if already_held {
        return Ok(ctx.heap().alloc_array(vec![Value::I64(TRY_LOCK_CONTENDED)]));
    }
    let arc = ctx.core.rwlocks.lock().get(&slot).cloned()
        .ok_or_else(|| anyhow!("__rwlock_try_read: unknown slot id {slot}"))?;
    match arc.try_read() {
        Some(guard) => {
            let cloned = (*guard).clone();
            std::mem::forget(guard);
            HELD_RWLOCK_GUARDS.with(|cell| {
                cell.borrow_mut().insert(slot, RwLockHeld::Read(Arc::clone(&arc)));
            });
            Ok(ctx.heap().alloc_array(vec![Value::I64(TRY_LOCK_OK), cloned]))
        }
        None => Ok(ctx.heap().alloc_array(vec![Value::I64(TRY_LOCK_CONTENDED)])),
    }
}

const TRY_LOCK_OK:        i64 = 0;
const TRY_LOCK_CONTENDED: i64 = 1;
```

z42 facade for RwLock:

```z42
public bool TryRead(Action<T> body) {
    object[] result = RwLockNative.TryRead(this._slot);
    long kind = (long)result[0];
    if (kind != 0) { return false; }
    T snapshot = (T)result[1];
    body(snapshot);
    RwLockNative.ReadRelease(this._slot);
    return true;
}

public bool TryWrite(Func<T, T> body) {
    object[] result = RwLockNative.TryWrite(this._slot);
    long kind = (long)result[0];
    if (kind != 0) { return false; }
    T current = (T)result[1];
    T next    = body(current);
    RwLockNative.WriteStore(this._slot, next);
    RwLockNative.WriteRelease(this._slot);
    return true;
}
```

## Testing Strategy

- **Rust unit**: 6+ tests across try_send / try_read / try_write
- **z42 stdlib**: 3 tests in `tests/try_variants.z42`
- **Cross-thread**: covered by existing rwlock_basic concurrent tests
  (this spec just adds the non-blocking paths)

## Deferred / Future Work

### `add-sync-primitives-send-timeout`
- timeout-based variants for send/recv

### `add-sync-primitives-mutex-try`
- `Mutex.TryLock(Func<T,T>) -> bool` (if user demand emerges)
