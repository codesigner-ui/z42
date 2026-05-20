# Design: Bounded MPSC channels

## Architecture

```
当前（after add-sync-primitives）：
  ChannelSlot {
      sender:   Option<mpsc::Sender<Value>>,
      receiver: Arc<Mutex<mpsc::Receiver<Value>>>,
  }
  __channel_new() → unbounded via mpsc::channel()

本 spec 后：
  enum ChannelSender {
      Unbounded(mpsc::Sender<Value>),
      Bounded(mpsc::SyncSender<Value>),
  }

  ChannelSlot {
      sender:   Option<ChannelSender>,
      receiver: Arc<Mutex<mpsc::Receiver<Value>>>,
  }

  __channel_new()              → ChannelSender::Unbounded(tx)
  __channel_new_bounded(cap)   → ChannelSender::Bounded(tx)
  __channel_send: dispatch on sender variant
```

`mpsc::Receiver` is the same type for both, so `__channel_recv` /
`__channel_try_recv` / `__channel_close` are completely unchanged.

## Decisions

### Decision 1: capacity == 0 (rendezvous channel)

**问题**：Rust `mpsc::sync_channel(0)` 创建 rendezvous channel —— send 完全
阻塞直到 recv 同步取走（buffer 永远 0）。是否暴露？

**决定**：**接受 capacity 0 不特殊处理**。理由：
- 符合 std::sync::mpsc 文档语义
- 用户若传 0，得到 rendezvous 行为是 Rust 设计意图
- 不需要参数校验异常路径
- z42 facade 文档注释明记 "capacity 0 = rendezvous (every Send blocks until paired Recv)"

### Decision 2: z42 facade naming

**问题**：`Channel<T>.WithCapacity(N)` vs `new BoundedChannel<T>(N)` vs 构造器重载？

**选项**：
- **A** `Channel<T>.WithCapacity(N)` static factory — 单一 Channel<T> 类型
- **B** `new BoundedChannel<T>(N)` — 分离类型
- **C** `new Channel<T>(N)` 构造器重载

**决定**：**A**。理由：
- bounded/unbounded 对 API 调用方透明（Send / Recv / Close / TryRecv 行为
  相同；唯一差异是 Send 在 full 时阻塞 vs 永远立即返回）—— 不需要类型层区分
- A 与 Rust `crossbeam::channel::bounded(N)` / `unbounded()` 风格对齐
- C 在 z42 当前没有构造器重载支持的情况下需引入；A 用静态工厂避免依赖
- B 引入冗余类型 + 用户疑惑（"是否能赋值给 Channel<T>"）

### Decision 3: SendError 处理

**问题**：bounded `SyncSender::send` 在 channel disconnect 时返回 `SendError`，与
unbounded `Sender::send` 一致。但 send 阻塞时间无限。是否引入 timeout？

**决定**：v0 不引入 timeout。理由：
- timeout 需要 `send_timeout(Duration)` API，又是新工作面
- 多线程用户的"卡住"问题可通过 `Std.Threading.Thread.Join()` 超时手动诊断
- timeout 作为 deferred `add-sync-primitives-send-timeout`

### Decision 4: `ChannelSender` enum vs separate fields

**问题**：内部存 `Option<Sender>` + `Option<SyncSender>`，还是用 enum 区分？

**决定**：**用 enum**。理由：
- 一个 channel 永远是 bounded XOR unbounded，不是 union；enum 表达更准
- 给 `__channel_send` 一个 clean match arm 列举两种 variant
- close 时把 enum 整体置 None（仍是 `Option<ChannelSender>`），protocol 不变

## Implementation Notes

```rust
// src/runtime/src/corelib/sync.rs

pub(crate) enum ChannelSender {
    Unbounded(mpsc::Sender<Value>),
    Bounded(mpsc::SyncSender<Value>),
}

pub(crate) struct ChannelSlot {
    pub(crate) sender:   Option<ChannelSender>,
    pub(crate) receiver: Arc<std::sync::Mutex<mpsc::Receiver<Value>>>,
}

pub fn builtin_channel_new(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let (tx, rx) = mpsc::channel::<Value>();
    let id = ctx.core.next_channel_id.fetch_add(1, Ordering::Relaxed);
    ctx.core.channels.lock().insert(id, ChannelSlot {
        sender:   Some(ChannelSender::Unbounded(tx)),
        receiver: Arc::new(std::sync::Mutex::new(rx)),
    });
    Ok(Value::I64(id as i64))
}

pub fn builtin_channel_new_bounded(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let cap = match args.first() {
        Some(Value::I64(n)) if *n >= 0 => *n as usize,
        Some(other) => bail!("__channel_new_bounded: expected i64 capacity >= 0, got {:?}", other),
        None        => bail!("__channel_new_bounded: missing capacity argument"),
    };
    let (tx, rx) = mpsc::sync_channel::<Value>(cap);
    let id = ctx.core.next_channel_id.fetch_add(1, Ordering::Relaxed);
    ctx.core.channels.lock().insert(id, ChannelSlot {
        sender:   Some(ChannelSender::Bounded(tx)),
        receiver: Arc::new(std::sync::Mutex::new(rx)),
    });
    Ok(Value::I64(id as i64))
}

pub fn builtin_channel_send(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__channel_send")?;
    let val  = args.get(1)
        .ok_or_else(|| anyhow!("__channel_send: missing value argument"))?
        .clone();

    enum SendHandle {
        Unbounded(mpsc::Sender<Value>),
        Bounded(mpsc::SyncSender<Value>),
    }
    let handle: SendHandle = {
        let map = ctx.core.channels.lock();
        let chan = map.get(&slot)
            .ok_or_else(|| anyhow!("__channel_send: unknown slot id {slot}"))?;
        let sender = chan.sender.as_ref()
            .ok_or_else(|| anyhow!("__channel_send: channel {slot} is closed"))?;
        match sender {
            ChannelSender::Unbounded(tx) => SendHandle::Unbounded(tx.clone()),
            ChannelSender::Bounded(tx)   => SendHandle::Bounded(tx.clone()),
        }
    };
    // Registry lock released. Bounded send may block while full; unbounded
    // returns immediately. Both error on disconnect.
    let send_result = match handle {
        SendHandle::Unbounded(tx) => tx.send(val).map_err(|_| ()),
        SendHandle::Bounded(tx)   => tx.send(val).map_err(|_| ()),
    };
    send_result.map_err(|_| anyhow!("__channel_send: channel {slot} disconnected"))?;
    Ok(Value::Null)
}
```

`__channel_close` becomes a tiny match-ignore (we just `take` the whole
`Option<ChannelSender>` regardless of variant):

```rust
pub fn builtin_channel_close(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = slot_id_arg(args, 0, "__channel_close")?;
    let mut map = ctx.core.channels.lock();
    let chan = map.get_mut(&slot)
        .ok_or_else(|| anyhow!("__channel_close: unknown slot id {slot}"))?;
    chan.sender = None;  // drops the ChannelSender enum
    Ok(Value::Null)
}
```

### z42 facade

```z42
public class Channel<T> {
    private long _slot;

    // Existing unbounded constructor.
    public Channel() {
        this._slot = ChannelNative.New();
    }

    // Private constructor used by WithCapacity (z42 doesn't have factory-only
    // visibility annotations; we keep it private so users go through WithCapacity).
    private Channel(long slot) {
        this._slot = slot;
    }

    // Bounded factory. `capacity == 0` creates a rendezvous channel (every
    // Send blocks until a paired Recv).
    public static Channel<T> WithCapacity(int capacity) {
        long slot = ChannelNative.NewBounded((long)capacity);
        return new Channel<T>(slot);
    }

    // ... Send / Recv / TryRecv / Close unchanged ...
}

public static class ChannelNative {
    [Native("__channel_new")]
    public static extern long New();

    [Native("__channel_new_bounded")]
    public static extern long NewBounded(long capacity);

    // ... existing Native bindings ...
}
```

## Testing Strategy

- **Rust unit** (`corelib/sync_tests.rs`):
  - `channel_new_bounded_returns_slot_id`
  - `channel_new_bounded_invalid_arg_errors` (missing / wrong type)
  - `channel_bounded_send_recv_round_trip`
  - `channel_bounded_close_then_recv_returns_discriminator_2`
- **z42 stdlib** (`tests/channel_bounded.z42`):
  - `test_bounded_basic_send_recv` — capacity=2, send 2, recv 2, no blocking
  - `test_bounded_back_pressure_with_slow_consumer` — capacity=2, fast
    producer + main thread slow consumer; producer thread eventually waits
    for consumer; test completes if back-pressure works (no OOM, FIFO order)

## Deferred / Future Work

### `add-sync-primitives-try-send`
- **来源**: 本 spec Out of Scope
- **触发原因**: bounded channel 的 `try_send` API（不阻塞，full 时立即返）
- **触发条件**: 用户需要 polling-based back-pressure 处理

### `add-sync-primitives-send-timeout`
- **来源**: Decision 3
- **触发原因**: bounded send with timeout
- **前置依赖**: z42.time.TimeSpan + bounded channel infrastructure
