# Proposal: Bounded MPSC channels (`Channel<T>` capacity overload)

## Why

`add-sync-primitives` (2026-05-20) ships unbounded MPSC channels via
`std::sync::mpsc::channel()`. Unbounded works for many cases but has a
critical hole: **fast producers can grow the channel indefinitely**,
exhausting heap before the consumer catches up. Real systems need
back-pressure — `Send` should block when the queue is full and resume
when the consumer makes room.

Rust's standard library provides exactly this via `mpsc::sync_channel(cap)`
(returns the same `Sender` / `Receiver` types but with a fixed-size buffer
+ blocking Send semantics). Wiring this into z42 is a small, contained
extension that fills the obvious gap left by `add-sync-primitives`
Decision 2 (which deferred bounded channels as future work).

## What Changes

- **New builtin `__channel_new_bounded(capacity: i64) -> i64 slot_id`** —
  creates a `mpsc::sync_channel(cap)`-backed slot. Returns the slot id
  the same way `__channel_new` does.
- **`__channel_send` semantics unchanged for unbounded channels**;
  bounded channels block when full (which is the documented behavior of
  `mpsc::SyncSender::send`). Sender doesn't need a new builtin — the
  underlying `SendError` semantics map cleanly to existing
  "disconnected" surfacing.
- **z42 `Channel<T>` gains a `WithCapacity(int capacity)` static factory**
  that calls `__channel_new_bounded`. Existing parameter-less constructor
  `new Channel<T>()` stays unchanged (unbounded).
- **`Send` blocks the calling thread when the bounded channel is full**;
  unbounded `Send` semantics unchanged.
- **2 stdlib tests** covering bounded send/recv pairing + back-pressure
  (slow consumer + fast producer ends with bounded queue, not OOM).

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/sync.rs` | MODIFY | `ChannelSlot` 扩展为 enum 区分 `Unbounded(mpsc::Sender, ...)` / `Bounded(mpsc::SyncSender, ...)`；新 builtin `builtin_channel_new_bounded`；`builtin_channel_send` 内根据 sender variant 分发 |
| `src/runtime/src/corelib/sync_tests.rs` | MODIFY | 加 4+ 单测：bounded new 返 slot_id / bounded send blocks when full / bounded send-recv pairing / bounded close-then-recv discriminator 2 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__channel_new_bounded` builtin（追加末尾保留既有 BuiltinId） |
| `src/libraries/z42.threading/src/Channel.z42` | MODIFY | 加 `Channel<T>.WithCapacity(int capacity) -> Channel<T>` static factory + `ChannelNative.NewBounded(long)` [Native] 声明 |
| `src/libraries/z42.threading/tests/channel_bounded.z42` | NEW | 2 stdlib tests：bounded basic send/recv + producer-consumer with capacity=2 验证 back-pressure |
| `docs/design/stdlib/organization.md` | MODIFY | `z42.threading` 行简短补一句 bounded channel |
| `docs/spec/changes/add-sync-primitives-bounded-channel/` | NEW | 本 spec |

**只读引用**：

- `src/runtime/src/corelib/sync.rs` 现有 `ChannelSlot` 设计
- `docs/spec/archive/2026-05-20-add-sync-primitives/design.md` Decision 2

## Out of Scope

- **Channel API 突破**：`Channel<T>` 类型本身不变；只通过额外的工厂方法
  暴露 bounded 变体。bounded vs unbounded 对调用方透明（`Send` 一样调用）
- **try_send`（非阻塞 bounded send）**：标准 `mpsc::SyncSender::try_send`
  存在但 v0 不暴露；用 unbounded channel 或 try_recv 检查 + 间接做。
  `add-sync-primitives-try-send` 作为 deferred follow-up
- **改 trait 签名 / 改 BUILTIN_INDEX 排序**：纯 append-end 保 BuiltinId 稳定

## Open Questions

- [ ] **capacity == 0 怎么办**：Rust `mpsc::sync_channel(0)` 给 rendezvous
      channel（send 阻塞直到 recv 同步取走）。是否暴露？Decision 1
- [ ] **z42 facade 的命名**：`Channel<T>.WithCapacity(N)` vs `new BoundedChannel<T>(N)`
      vs `new Channel<T>(N)`（构造器重载）？Decision 2
