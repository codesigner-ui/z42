# Tasks: Non-blocking try-variants

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm + feat

## 进度概览
- [x] 阶段 1: __channel_try_send + __rwlock_try_read + __rwlock_try_write
- [x] 阶段 2: builtin 注册 + Rust 单测
- [x] 阶段 3: z42 facade (TrySend / TryRead / TryWrite)
- [x] 阶段 4: stdlib tests (try_variants.z42)
- [x] 阶段 5: 归档 + commit + push

## 阶段 1: builtins

- [x] 1.1 `corelib/sync.rs` 加 const `TRY_SEND_OK/FULL/DISCONNECTED` 和 `TRY_LOCK_OK/CONTENDED`
- [x] 1.2 `builtin_channel_try_send` —— enum-dispatch on ChannelSender；Unbounded 走 send，Bounded 走 try_send
- [x] 1.3 `builtin_rwlock_try_read` —— reject reentrancy + arc.try_read() + thread-local Read variant
- [x] 1.4 `builtin_rwlock_try_write` —— 同上但 Write variant
- [x] 1.5 cargo build GREEN

## 阶段 2: 注册 + 单测

- [x] 2.1 `corelib/mod.rs` 注册 3 builtin（追加末尾）
- [x] 2.2 `corelib/sync_tests.rs` 加 6+ 单测：
        `channel_try_send_unbounded_succeeds`
        `channel_try_send_bounded_full_returns_1`
        `channel_try_send_closed_returns_2`
        `rwlock_try_read_uncontended_succeeds`
        `rwlock_try_read_reentrancy_rejected`
        `rwlock_try_write_uncontended_succeeds`
        `rwlock_try_write_during_outstanding_read_returns_contended`
- [x] 2.3 cargo test 全过

## 阶段 3: z42 facade

- [x] 3.1 `Channel.z42` 加 `public bool TrySend(T v)` 方法 + `ChannelNative.TrySend(long, object) -> long` [Native]
- [x] 3.2 `RwLock.z42` 加 `TryRead(Action<T>) -> bool` / `TryWrite(Func<T,T>) -> bool` + `RwLockNative.TryRead(long) -> object[]` / `TryWrite(long) -> object[]` [Native]
- [x] 3.3 ./scripts/build-stdlib.sh GREEN

## 阶段 4: stdlib tests

- [x] 4.1 `tests/try_variants.z42` NEW —— 3 测：
        `test_try_send_bounded_full_returns_false`
        `test_try_read_uncontended_succeeds`
        `test_try_write_uncontended_succeeds`
- [x] 4.2 ./scripts/test-stdlib.sh z42.threading GREEN
- [x] 4.3 ./scripts/test-stdlib.sh 全量 72/72（71 既有 + 1 新文件）不回归

## 阶段 5: 归档 + commit

- [x] 5.1 mv → `docs/spec/archive/2026-05-20-add-sync-primitives-try-variants/`
- [x] 5.2 ./scripts/test-all.sh ALL GREEN
- [x] 5.3 commit + push

## 备注

### 实施期发现 1 —— `try_read` / `try_write` 借用检查器问题

parking_lot::RwLock::try_read() 返回 `Option<RwLockReadGuard<'_, T>>`，guard 的
lifetime 借自 `&arc`。即使在 Some 分支内 `mem::forget(guard)`，借用检查器仍
认为 guard borrow 持续到 match 结束，要求 arc 至少存活到 match 结束 —— 但 arc
是函数局部变量，drop 顺序与 Option 的 Drop 冲突，编译错 E0597。

`read()` （非 try）版本能编通过的原因：直接返 guard 而非 Option，NLL 可
proof "mem::forget 之后 borrow released"。Option 多套了一层 Drop 信息丢失。

**修复**：改用 parking_lot::lock_api::RawRwLock 直接调用 `try_lock_shared()` /
`try_lock_exclusive()`，绕过 guard 类型；通过 `arc.data_ptr()` 直接读 Value。
配套 `__rwlock_*_release` 已用 `force_unlock_read/write` 走相同 raw 路径，
acquire/release 配对自然匹配。

`raw()` 调用本身 unsafe（parking_lot 把 raw API 标 unsafe 因为它绕过 guard
类型保证），加 `unsafe { arc.raw() }` 块 + SAFETY 注释说明 invariants
（thread-local map + force_unlock 配对）。

### 实施期发现 2 —— stdlib 文件 count

72/72 stdlib（71 + 1 新 try_variants.z42）。
