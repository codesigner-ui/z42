# Tasks: Std.Threading.RwLock<T>

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm + feat

## 进度概览
- [x] 阶段 1: VmCore 新字段 (rwlocks / next_rwlock_id)
- [x] 阶段 2: RwLockHeld enum + thread-local + 6 builtins
- [x] 阶段 3: builtin 注册 + 单测
- [x] 阶段 4: z42 facade (RwLock.z42 + RwLockNative)
- [x] 阶段 5: stdlib tests (rwlock_basic.z42)
- [x] 阶段 6: 文档同步
- [x] 阶段 7: 归档 + commit + push

## 阶段 1: VmCore 字段

- [x] 1.1 `vm_context.rs` VmCore 加 `rwlocks: Mutex<HashMap<u64, Arc<parking_lot::RwLock<Value>>>>` + `next_rwlock_id: AtomicU64`
- [x] 1.2 构造路径初始化两字段
- [x] 1.3 cargo build GREEN

## 阶段 2: RwLockHeld + builtins

- [x] 2.1 `corelib/sync.rs` 加 `enum RwLockHeld { Read(Arc<...>), Write(Arc<...>) }` + thread-local `HELD_RWLOCK_GUARDS: RefCell<HashMap<u64, RwLockHeld>>`
- [x] 2.2 `builtin_rwlock_new` —— 类似 mutex_new
- [x] 2.3 `builtin_rwlock_read_acquire` —— arc.read() + mem::forget + thread-local Read variant
- [x] 2.4 `builtin_rwlock_read_release` —— remove thread-local + match Read → force_unlock_read；match Write → bail
- [x] 2.5 `builtin_rwlock_write_acquire` —— arc.write() + mem::forget + thread-local Write variant
- [x] 2.6 `builtin_rwlock_write_store` —— 校验 thread-local 是 Write 变体；data_ptr 写
- [x] 2.7 `builtin_rwlock_write_release` —— 类似 read_release 但 Write 路径

## 阶段 3: 注册 + 单测

- [x] 3.1 `corelib/mod.rs` 注册 6 个 builtin（追加末尾）
- [x] 3.2 `corelib/sync_tests.rs` 加 6+ 单测：
        `rwlock_new_returns_monotonic_slot_ids`
        `rwlock_read_acquire_then_release_no_op`
        `rwlock_write_store_then_read_observes_new_value`
        `rwlock_write_store_without_write_acquire_errors`
        `rwlock_read_release_when_held_write_errors`
        `rwlock_unknown_slot_errors`
- [x] 3.3 cargo test 全过

## 阶段 4: z42 facade

- [x] 4.1 `src/libraries/z42.threading/src/RwLock.z42` NEW —— `Std.Threading.RwLock<T>` (Read/Write callback API) + `RwLockNative` static class (6 [Native] 声明)
- [x] 4.2 ./scripts/build-stdlib.sh GREEN

## 阶段 5: stdlib tests

- [x] 5.1 `tests/rwlock_basic.z42` NEW —— 3 测：
        `test_single_thread_read_write_cycle`
        `test_concurrent_readers_observe_consistent`
        `test_writer_blocks_readers`
- [x] 5.2 ./scripts/test-stdlib.sh z42.threading GREEN
- [x] 5.3 ./scripts/test-stdlib.sh 全量 71/71（70 既有 + 1 新文件）不回归

## 阶段 6: 文档同步

- [x] 6.1 `docs/design/stdlib/organization.md` `z42.threading` 行补 RwLock
- [x] 6.2 `docs/design/runtime/vm-architecture.md` VmCore 字段表加 `rwlocks` 行
- [x] 6.3 `src/libraries/z42.threading/README.md` 加 RwLock 行

## 阶段 7: 归档 + commit

- [x] 7.1 mv → `docs/spec/archive/2026-05-20-add-sync-primitives-rwlock/`
- [x] 7.2 ./scripts/test-all.sh ALL GREEN
- [x] 7.3 commit + push

## 备注

（实施中发现写这里）
