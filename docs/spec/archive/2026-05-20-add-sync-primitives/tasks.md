# Tasks: add Std.Threading sync primitives (Mutex / Channel)

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm + feat

## 进度概览
- [x] 阶段 1: VmCore 新字段 (mutexes / next_mutex_id / channels / next_channel_id)
- [x] 阶段 2: Mutex native fns + 单测
- [x] 阶段 3: Channel native fns + 单测
- [x] 阶段 4: 集成测试 (cross_thread_smoke.rs)
- [x] 阶段 5: z42 stdlib classes (Mutex / Channel / ChannelDisconnectedException)
- [x] 阶段 6: z42 stdlib tests (mutex_basic / channel_basic / channel_disconnect)
- [x] 阶段 7: 文档同步
- [x] 阶段 8: 归档 + commit + push

## 阶段 1: VmCore 字段

- [x] 1.1 `src/runtime/src/vm_context.rs` VmCore 加 4 字段：
        `mutexes: Mutex<HashMap<u64, Arc<parking_lot::Mutex<Value>>>>`
        `next_mutex_id: AtomicU64`
        `channels: Mutex<HashMap<u64, ChannelSlot>>`
        `next_channel_id: AtomicU64`
- [x] 1.2 `ChannelSlot` struct 定义在 `corelib/sync.rs` 内（用 pub(crate)），
        VmCore 字段类型引用之
- [x] 1.3 构造路径（`new()` / `with_module()` / `new_with_core` 不需要 — `new_with_core` 复用 core 不构造）初始化 4 字段
- [x] 1.4 cargo build GREEN

## 阶段 2: Mutex native fns

- [x] 2.1 `src/runtime/src/corelib/sync.rs` NEW —— `builtin_mutex_new` /
        `builtin_mutex_lock_acquire` / `builtin_mutex_store` / `builtin_mutex_unlock`
        - 关键：用 thread-local `HELD_GUARDS: RefCell<HashMap<u64, *mut Value>>`
          存当前线程持有的 lock guard 指针；Box::leak + transmute lifetime 'static；
          unlock 时从 thread-local 取回 + reconstruct guard + drop
- [x] 2.2 实施期验证 Box::leak / transmute 路径是否真能跨 builtin 调用持锁；
        若不行回 Decision 1 amendment（callback-only `__mutex_with_lock`）
- [x] 2.3 `corelib/sync_tests.rs` NEW —— 6+ 单测（参数校验 / 同线程 lock-store-unlock）
- [x] 2.4 `corelib/mod.rs` 注册 4 builtin（key: `__mutex_new` / `__mutex_lock_acquire` /
        `__mutex_store` / `__mutex_unlock`）
- [x] 2.5 cargo test 全过

## 阶段 3: Channel native fns

- [x] 3.1 `corelib/sync.rs` 加 `builtin_channel_new` / `builtin_channel_send` /
        `builtin_channel_recv` / `builtin_channel_try_recv` / `builtin_channel_close`
- [x] 3.2 mpsc::Sender 多线程 clone 每次 send 一份
- [x] 3.3 mpsc::Receiver 多线程 race 用内部 std::sync::Mutex 串行
- [x] 3.4 try_recv 返回 discriminator array：`[I64(0), v]` ok / `[I64(1)]` empty / `[I64(2)]` disconnected
- [x] 3.5 `corelib/sync_tests.rs` 加 6+ 单测（send-recv / close-then-recv-throws / try_recv empty）
- [x] 3.6 `corelib/mod.rs` 注册 5 builtin
- [x] 3.7 cargo test 全过

## 阶段 4: 集成测试

- [x] 4.1 `runtime/tests/cross_thread_smoke.rs` 加 `mutex_serializes_concurrent_increments`
        （2 workers × 100 increments, 验证 final == 200）
- [x] 4.2 加 `channel_producer_consumer_hand_off`（producer thread 5 send；consumer
        main thread 5 recv，FIFO 验证）
- [x] 4.3 cargo test 全过

## 阶段 5: z42 stdlib classes

- [x] 5.1 `src/libraries/z42.threading/src/Mutex.z42` NEW —— `Std.Threading.Mutex<T>`
        public class + `MutexNative` static class
- [x] 5.2 `src/libraries/z42.threading/src/Channel.z42` NEW —— `Std.Threading.Channel<T>` +
        `ChannelNative` static class
- [x] 5.3 `src/libraries/z42.threading/src/ChannelDisconnectedException.z42` NEW ——
        namespace Std；继承 Exception
- [x] 5.4 `src/libraries/z42.threading/README.md` 更新（加 Mutex / Channel 行）
- [x] 5.5 build-stdlib.sh / build-stdlib.z42 不需要变更（z42.threading 已 registered）
- [x] 5.6 ./scripts/build-stdlib.sh GREEN

## 阶段 6: stdlib tests

- [x] 6.1 `tests/mutex_basic.z42` —— 单线程 lock + 跨线程 counter (2 workers ×
        N increments)
- [x] 6.2 `tests/channel_basic.z42` —— 同线程 send/recv；多 send 顺序；TryRecv empty
- [x] 6.3 `tests/channel_disconnect.z42` —— Close 后 Recv 抛 ChannelDisconnectedException
- [x] 6.4 ./scripts/test-stdlib.sh z42.threading GREEN（既有 4 + 新 3 = 7 file）
- [x] 6.5 ./scripts/test-stdlib.sh 全量 69/69 不回归

## 阶段 7: 文档同步

- [x] 7.1 `docs/design/runtime/concurrency.md` "同步原语" 行 ❌ → ✅；next-step
        spec list `add-sync-primitives` ✅
- [x] 7.2 `docs/design/stdlib/organization.md` `z42.threading` 行扩描述（Mutex / Channel）
- [x] 7.3 `docs/design/stdlib/roadmap.md` `z42.threading.sync` 占位 → 已落地
- [x] 7.4 `docs/design/runtime/vm-architecture.md` VmCore 字段表加 `mutexes` /
        `next_mutex_id` / `channels` / `next_channel_id` 4 行

## 阶段 8: 归档 + commit

- [x] 8.1 mv → `docs/spec/archive/2026-05-20-add-sync-primitives/`
- [x] 8.2 commit + push（建议分 commit：阶段 1+2 / 阶段 3+4 / 阶段 5+6+7+8）
- [x] 8.3 verify CI GREEN

## 备注

### 实施期发现 1 —— Scope 扩展 `gc/refs.rs`

阶段 6 实施 `test_two_workers_concurrent_increments_serialised` 时发现：两个 worker 并发 field_get 同一 shared `Mutex<long>` instance 时，`GcRef::borrow()` 内部 `try_lock().expect(...)` panic — 原代码注释明确假设"different-thread access won't happen"（add-multithreading-foundation Phase 3 从 RefCell 迁移到 parking_lot::Mutex 时保留的过度保守 try_lock 语义）。

经 User 裁决（option A）扩展 Scope 加 `src/runtime/src/gc/refs.rs`，把 `borrow()` / `borrow_mut()` 从 `try_lock().expect` 改为 blocking `lock()`：

- 多线程并发访问同一 GcRef 正确阻塞等待而非 panic
- 同线程递归 borrow 现在 deadlock（与 std Rust Mutex 语义对齐；RefCell 风格的递归检测是迁移期遗留，real Rust Mutex 从未提供）
- 这是 add-threading-stdlib 落地后浮现的 latent regression，根因修复

### 实施期发现 2 —— `__channel_recv` 协议改为 discriminator-array

原 design 把 `__channel_recv` 设计为 `Result<Value>` 直接返回 Value 成功 / `bail!` 失败。实施期发现：z42 用户层 `catch (ChannelDisconnectedException e)` 只能 catch 通过 throw 路径产生的 Value::Object 异常，无法从 anyhow `bail!` 翻译。把 `__channel_recv` 改为返回 discriminator array `[I64(0), v]` / `[I64(2)]`，让 z42 facade `Recv()` 检查 discriminator + `throw new ChannelDisconnectedException(...)`。与 `__channel_try_recv` 协议自然统一。

### 实施期发现 3 —— closure 值类型按 snapshot 捕获

阶段 6 写 Mutex tests 一度用 `long final = 0; m.Lock((long v) => { final = v; ... })` 读出值——但 closure.md §4.1 规定值类型按 snapshot 捕获，写入不逃逸。改用 static field `MutexState.Observed`（§4.4 推荐路径）。z42 closure 设计明确："共享可变状态的唯一推荐路径是 class（引用类型）"。

### 实施期发现 4 —— `VmContext::core` 字段对集成测试不可见

`cross_thread_smoke.rs` 是独立 crate，无法访问 `pub(crate)` 字段 `ctx.core`。加 `pub fn core_arc(&self) -> Arc<VmCore>` 公开 accessor。
