# Design: Std.Threading.Mutex<T> / Channel<T>

## Architecture

```
当前（after add-threading-stdlib）：
  VmCore {
    static_fields, ..., heap, vm_contexts,
    module: Option<Arc<Module>>,
    threads: Mutex<HashMap<u64, JoinHandle<...>>>,
    next_thread_id: AtomicU64,
  }

本 spec 后：
  VmCore {
    ..., threads, next_thread_id,
    mutexes:        Mutex<HashMap<u64, parking_lot::Mutex<Value>>>,   ← NEW
    next_mutex_id:  AtomicU64,                                          ← NEW
    channels:       Mutex<HashMap<u64, ChannelSlot>>,                   ← NEW
    next_channel_id: AtomicU64,                                          ← NEW
  }

  struct ChannelSlot {
    sender:   Option<std::sync::mpsc::Sender<Value>>,
    receiver: std::sync::Mutex<std::sync::mpsc::Receiver<Value>>,
    // Mutex around Receiver because multiple Recv callers could race; mpsc
    // only allows single-consumer-at-a-time. With our slot-id-based access
    // multiple threads can call Recv on the same channel; Mutex serializes.
  }
```

## Decisions

### Decision 1: Mutex API 形态 —— RAII callback vs 显式 Lock/Unlock

**问题**：用户怎么写一个 Mutex critical section？

**选项**：
- **A** RAII callback：`m.Lock((v) => v + 1)` —— Func<T, T> 返回新值，runtime 写回 + 自动 unlock
- **B** 显式 pair：`var g = m.Lock(); g.Set(g.Get() + 1); g.Unlock();` —— 用户自己保证 unlock
- **C** Sentinel block：`using (m.Lock()) { ... }` —— 需要 `using` 语法

**决定**：**A**。callback 风格：
- ✅ 不可能忘记 unlock（runtime 在 callback 返回后 unconditionally unlock）
- ✅ Panic-safe via parking_lot（不 poison）
- ✅ 不需要新语法（`using` 是 L3）
- ⚠️ 不支持 `await` 跨 callback；v0 没有 async，未来 spec 处理

API:
```z42
public class Mutex<T> {
    private long _slot;
    public Mutex(T initial) { ... }
    public void Lock(Func<T, T> body) { ... }
    public T Get() { ... }  // 非锁读，仅 debug；竞态下值有滞后
}
```

### Decision 2: Channel 容量 —— unbounded vs bounded

**问题**：`new Channel<T>()` 默认容量是？

**选项**：
- **A** 默认 unbounded (`std::sync::mpsc::channel()`) —— Rust 默认行为
- **B** 必须指定 capacity (`new Channel<T>(100)` 用 `sync_channel`) —— 防 memory leak
- **C** unbounded 默认 + 可选 capacity overload

**决定**：**A**。v0 unbounded only。理由：
- 用户层 OS 线程当前轻量级（不会 spawn 千万级 producer）
- bounded channel 需要 `try_send` 分支处理 + backpressure 设计，超 v0 scope
- 后续 spec（`add-bounded-channel`）按需扩展为 C

### Decision 3: Channel<T> 元素类型约束

**问题**：`T` 能是任意 Value 吗？

**决定**：v0 不静态检查；运行时所有 Value 通过 Arc/Mutex backing 已 Send + Sync
（add-multithreading-foundation 落地）。TypeChecker 把 `Channel<T>` 当普通
generic class 实例化。

后续若引入 `Send` / `Sync` trait（L3），可加约束 `Channel<T> where T: Send`。

### Decision 4: parking_lot::Mutex 不 poison

**问题**：std::sync::Mutex 在 lock holder panic 时 poison —— 之后 `lock()`
返回 `LockResult::Err(PoisonError)`。我们用 parking_lot 不 poison —— panic
后 Mutex 状态正常。语义差异需要 design 记录。

**决定**：接受 parking_lot 行为。理由：
- z42 panic = Rust panic (catch_unwind in __thread_spawn) → worker `Result::Err`
- 用户 throw 通过 ExecOutcome::Thrown 上报，**不**触发 Rust panic
- 因此正常路径下 Mutex 永远不会被 panic 中断；poison 概念无价值
- parking_lot 性能更好 + API 更简单

design 备注：极端情况（runtime 内部 unsafe panic）下 Mutex 状态可能不一致，
但那一刻整个进程已经 abort（catch_unwind 兜底也只到 thread 级别）。

### Decision 5: Channel sender 多实例

**问题**：mpsc channel 只能有一个 Sender 句柄但可以 `clone()`。如果两个
线程都 send 怎么办？

**决定**：v0 channels 内只存一个 Sender（不 expose clone）。`__channel_send`
所有线程都通过 `VmCore.channels` slot 查到同一个 Sender 引用，
但 std::mpsc::Sender: Sync only via clone — 实际我们需要 `mpsc::Sender::clone()`
拿到 thread-local copy。

实施：`ChannelSlot { sender: Option<mpsc::Sender<Value>>, ... }`，
`__channel_send` 内 `sender.as_ref().ok_or(...)?.clone().send(v)` — 每次
send 做 clone（mpsc::Sender clone 极便宜，只是 Arc 引用计数+1）。

### Decision 6: Close 语义

**问题**：z42 用户怎么"关闭" channel？

**决定**：`__channel_close` builtin。`take` 出 Sender 置 None；剩余 receiver
读完缓冲后下次 Recv 拿到 `RecvError`（disconnected），翻译为 ChannelDisconnectedException。

z42 API：`channel.Close()` 调用 `__channel_close`。

## Implementation Notes

### Mutex 内部状态

```rust
pub struct VmCore {
    ...
    pub(crate) mutexes:        Mutex<HashMap<u64, Arc<parking_lot::Mutex<Value>>>>,
    pub(crate) next_mutex_id:  AtomicU64,
    ...
}

// __mutex_new(initial: Value) → I64
//   1. id = next_mutex_id.fetch_add(1)
//   2. mutexes.lock().insert(id, Arc::new(parking_lot::Mutex::new(initial)))
//   3. return I64(id)
//
// __mutex_lock(slot: I64) → Value
//   1. arc = mutexes.lock().get(&slot)?.clone()  // 拿到 Arc<parking_lot::Mutex>
//   2. drop registry lock immediately (key step — 否则 hold registry 锁 + 等内部 Mutex = 全局串行)
//   3. let mut guard = arc.lock()    // blocking acquire
//   4. clone current value, leak guard via Box::leak storing pointer in
//      thread-local 'held mutex slot' map
//   5. return cloned value
//
// __mutex_store(slot: I64, new_val: Value) → Null
//   1. lookup leaked guard from thread-local
//   2. *guard = new_val
//
// __mutex_unlock(slot: I64) → Null
//   1. drop leaked guard (re-Box + drop) → unlock
//   2. remove from thread-local held map
```

**关键问题**：parking_lot::Mutex.lock() 返回 `MutexGuard<'_, T>` 生命周期
绑定到 Mutex 本身。我们需要 cross-native-call 持有 lock — 不能简单借用
栈上。解决：用 `Arc<parking_lot::Mutex<Value>>` + `Box::leak`：

```rust
// __mutex_lock:
let arc: Arc<parking_lot::Mutex<Value>> = mutexes.lock().get(&slot)?.clone();
let guard = arc.lock();  // MutexGuard<'_, Value> tied to arc lifetime
// SAFETY: arc lives in HashMap until __mutex_unlock removes it.
// We "leak" the guard by transmuting lifetime to 'static + Box::leak.
let leaked: &'static mut Value = unsafe { std::mem::transmute(&mut *guard) };
std::mem::forget(guard);  // don't drop = don't unlock
HELD_GUARDS.with(|cell| cell.borrow_mut().insert(slot, leaked));
return Ok(value.clone());
```

→ 实施期发现 `Box::leak` 不行（arc 仍在 HashMap）；换 `parking_lot::lock_api::RawMutex`
+ 直接调 `raw().lock()` / `raw().unlock()` —— bypass guard 完全。这是 design
关键风险点；实施时若 RawMutex API 不通走 callback-only（去掉 Lock/Unlock 改纯 RAII）。

**Decision 1 amendment 风险**：如果实施期 RawMutex 路径走不通，回到纯 callback
（去掉 `__mutex_lock` / `__mutex_unlock` builtin，只留 `__mutex_with_lock(slot, action)` ——
原子 lock+callback+unlock。略损性能但安全）。

### Channel 内部状态

```rust
pub struct ChannelSlot {
    sender:   Option<std::sync::mpsc::Sender<Value>>,
    receiver: std::sync::Mutex<std::sync::mpsc::Receiver<Value>>,
}

// __channel_new() → I64
//   let (tx, rx) = mpsc::channel();
//   id = next_channel_id.fetch_add(1)
//   channels.lock().insert(id, ChannelSlot { sender: Some(tx), receiver: Mutex::new(rx) });
//   return I64(id)
//
// __channel_send(slot: I64, v: Value) → Null
//   let slot_guard = channels.lock();
//   let chan = slot_guard.get(&slot).ok_or("unknown slot")?;
//   let tx = chan.sender.as_ref().ok_or_else(|| anyhow!("channel closed"))?.clone();
//   drop(slot_guard);  // 释放 outer 锁后再 send（mpsc::send 是 fast path 通常不阻塞但仍非 trivial）
//   tx.send(v).map_err(|_| anyhow!("channel disconnected"))?;
//   return Null
//
// __channel_recv(slot: I64) → Value
//   loop {
//     let slot_guard = channels.lock();
//     let chan = slot_guard.get(&slot).ok_or("unknown slot")?;
//     let rx_mutex = &chan.receiver;
//     // Receiver 多线程 race：用 internal Mutex 串行
//     let rx_guard = rx_mutex.lock().unwrap();  // poison-on-panic OK，rare
//     drop(slot_guard);  // 释放 outer，否则 Recv 会 deadlock 别的 send
//     match rx_guard.recv() {
//       Ok(v)  => return Ok(v),
//       Err(_) => bail!("ChannelDisconnectedException"),
//     }
//   }
//
// __channel_close(slot: I64) → Null
//   channels.lock().get_mut(&slot)?.sender = None;
//   return Null
```

### z42 API

```z42
public class Mutex<T> {
    private long _slot;

    public Mutex(T initial) {
        this._slot = MutexNative.New(initial);
    }

    public void Lock(Func<T, T> body) {
        T current = (T)MutexNative.LockAcquire(this._slot);
        T next    = body(current);
        MutexNative.Store(this._slot, next);
        MutexNative.Unlock(this._slot);
    }
}

public class Channel<T> {
    private long _slot;

    public Channel() {
        this._slot = ChannelNative.New();
    }

    public void Send(T v) { ChannelNative.Send(this._slot, v); }
    public T Recv() { return (T)ChannelNative.Recv(this._slot); }
    public void Close() { ChannelNative.Close(this._slot); }

    public object[] TryRecv() { return (object[])ChannelNative.TryRecv(this._slot); }
}
```

## Testing Strategy

- **Rust unit tests** (`corelib/sync_tests.rs`): builtin 参数校验 / mutex 单线程
  lock-store-unlock 循环 / channel send-recv 同线程
- **Cross-thread smoke** (`runtime/tests/cross_thread_smoke.rs`):
  `mutex_protects_concurrent_increments` (2 workers × 100 = 200) +
  `channel_producer_consumer_hand_off` (1 producer 5 send, 1 consumer 5 recv)
- **z42 stdlib tests** (`z42.threading/tests/`): 3 文件
  - `mutex_basic.z42` — 单线程 lock + 跨线程 counter (worker 各 +1 共 N 次)
  - `channel_basic.z42` — single send/recv; multi send 顺序 recv; TryRecv empty
  - `channel_disconnect.z42` — Close 后 Recv 抛 ChannelDisconnectedException
- **GREEN gate**：stdlib 66 + 3 = 69；cargo test 全过

## Deferred / Future Work

### `add-sync-primitives-future-condvar`
- **来源**: 本 spec scope 收紧
- **触发原因**: Condvar 需要"wait while holding lock + atomically release + wait
  + re-acquire" 协议，跟 Mutex API 形态强耦合；先把 Mutex 跑顺再决定
- **前置依赖**: 本 spec 落地
- **触发条件**: 生产代码出现"信号通知"场景（如 thread pool worker queue）

### `add-sync-primitives-future-bounded-channel`
- **来源**: Decision 2
- **触发原因**: v0 unbounded only；back-pressure 设计独立
- **触发条件**: 用户报内存膨胀，或引入 thread pool 时强制
