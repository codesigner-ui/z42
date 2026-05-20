# Tasks: Bounded MPSC channels

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm + feat

## 进度概览
- [x] 阶段 1: ChannelSender enum + ChannelSlot 重构
- [x] 阶段 2: `__channel_new_bounded` builtin + 注册
- [x] 阶段 3: `__channel_send` 分发 enum variant
- [x] 阶段 4: Rust 单测 + z42 stdlib 测试
- [x] 阶段 5: z42 Channel<T>.WithCapacity + ChannelNative.NewBounded
- [x] 阶段 6: 文档同步
- [x] 阶段 7: 归档 + commit + push

## 阶段 1: ChannelSender enum

- [x] 1.1 `corelib/sync.rs` `enum ChannelSender { Unbounded(mpsc::Sender<Value>), Bounded(mpsc::SyncSender<Value>) }`
- [x] 1.2 `ChannelSlot.sender` 类型从 `Option<mpsc::Sender>` → `Option<ChannelSender>`
- [x] 1.3 `builtin_channel_new` 把现有 `Some(tx)` 改为 `Some(ChannelSender::Unbounded(tx))`
- [x] 1.4 cargo build GREEN

## 阶段 2: builtin_channel_new_bounded

- [x] 2.1 `corelib/sync.rs` 加 `builtin_channel_new_bounded`：参数校验 + `mpsc::sync_channel(cap)` + 插入 slot
- [x] 2.2 `corelib/mod.rs` 注册 `__channel_new_bounded` builtin（追加末尾保留 BuiltinId 稳定）

## 阶段 3: builtin_channel_send 分发

- [x] 3.1 `builtin_channel_send` 内部 match `ChannelSender::Unbounded(tx)` / `Bounded(tx)` → 各自 `tx.clone().send(val)`
- [x] 3.2 cargo build GREEN

## 阶段 4: Rust 单测

- [x] 4.1 `corelib/sync_tests.rs` 加 4 测：
        `channel_new_bounded_returns_slot_id`
        `channel_new_bounded_invalid_arg_errors`
        `channel_bounded_send_recv_round_trip`
        `channel_bounded_close_then_recv_returns_discriminator_2`
- [x] 4.2 cargo test 全过（既有 channel 测试不回归）

## 阶段 5: z42 facade

- [x] 5.1 `src/libraries/z42.threading/src/Channel.z42` 加 `private Channel(long slot)` ctor + `public static Channel<T> WithCapacity(int capacity)` 工厂
- [x] 5.2 `ChannelNative` 加 `NewBounded(long capacity)` [Native] 声明
- [x] 5.3 `src/libraries/z42.threading/tests/channel_bounded.z42` NEW —— 2 测：
        `test_bounded_basic_send_recv`（capacity=2，send 2 + recv 2）
        `test_bounded_back_pressure_with_slow_consumer`（capacity=2 + 跨线程 producer+consumer 验证 FIFO + 无 OOM）
- [x] 5.4 ./scripts/build-stdlib.sh GREEN
- [x] 5.5 ./scripts/test-stdlib.sh z42.threading GREEN
- [x] 5.6 ./scripts/test-stdlib.sh 全量 71/71（69 既有 + 2 新 channel_bounded test）不回归

## 阶段 6: 文档同步

- [x] 6.1 `docs/design/stdlib/organization.md` `z42.threading` 行短句补 bounded channel

## 阶段 7: 归档 + commit

- [x] 7.1 mv → `docs/spec/archive/2026-05-20-add-sync-primitives-bounded-channel/`
- [x] 7.2 ./scripts/test-all.sh ALL GREEN
- [x] 7.3 commit + push

## 备注

### 实施期发现 1 —— z42 不支持 `ClassName<T>.StaticMethod()` 语法

Design Decision 2 选了 `Channel<T>.WithCapacity(int)` 静态工厂。实施期发现 z42 parser 不接受 `Channel<long>.WithCapacity(2)` 写法（`unexpected token "." in expression`）—— `<T>` 后直接接 `.` 调静态方法不在当前语法支持范围。

**改用构造器重载**：`Channel<T>` 加 `public Channel(int capacity)` 第二个构造器，调用方写 `new Channel<long>(2)`。验证表明 z42 支持构造器重载（基于 `ClassArityOverloadingTests` 中的 arity 重载机制，按参数计数 + 类型分发）。比 `WithCapacity` 静态工厂更符合 z42 现有语法，无需特殊支持。

z42 facade 同步调整：删除 `WithCapacity` + private slot ctor，改单一 public 构造器对 + 重载。

### 实施期发现 2 —— 文件 count 校对

proposal 写 "全量 71/71"，实际正确数字是 **70/70**（69 既有文件 + 1 新 channel_bounded.z42 文件）。channel_bounded.z42 内含 2 测，但 stdlib 计数按 *文件*，不按测试数。Spec 内 71 是 +2 测的误算。实际 GREEN gate 不受影响。
