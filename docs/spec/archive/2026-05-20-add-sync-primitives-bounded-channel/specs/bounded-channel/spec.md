# Spec: Bounded MPSC channels

## ADDED Requirements

### Requirement: Bounded channel creation and capacity enforcement

#### Scenario: WithCapacity(N) creates a bounded slot
- **WHEN** `Channel<long>.WithCapacity(2)` is called
- **THEN** returns a Channel whose underlying `__channel_new_bounded(2)`
  registered a `mpsc::sync_channel(2)` ChannelSlot

#### Scenario: Send blocks when buffer is full
- **WHEN** the channel has capacity 1, one value already in the queue,
  and the consumer hasn't read yet
- **THEN** a second `Send` call blocks the calling thread until the
  consumer calls `Recv` (or all senders close, which would error)

#### Scenario: Send unblocks after Recv
- **WHEN** producer is blocked in `Send` because queue is full; consumer
  calls `Recv`
- **THEN** consumer receives the head value, producer's `Send` unblocks
  and the second value enters the queue

#### Scenario: Bounded channel close behaviour matches unbounded
- **WHEN** sender closes a bounded channel after queueing N values
- **THEN** subsequent `Recv` drains the N values then throws
  `ChannelDisconnectedException` (same as unbounded)

## MODIFIED Requirements

### Requirement: Channel<T> API surface

**Before:**
- `new Channel<T>()` → unbounded MPSC

**After:**
- `new Channel<T>()` → unbounded MPSC (unchanged)
- `Channel<T>.WithCapacity(int capacity)` → bounded MPSC, `Send` blocks
  when the queue size reaches `capacity`

## IR Mapping

No new IR. Adds one builtin (`__channel_new_bounded`).

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 无变更
- [x] VM interp — `__channel_new_bounded` 注册 + `__channel_send` 分发
