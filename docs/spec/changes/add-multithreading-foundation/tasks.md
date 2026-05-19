# Tasks: add multi-threading foundation

> 状态：🟡 进行中 | 创建：2026-05-19 | 类型：vm

## 进度概览
- [ ] 阶段 1: VmCore 雏形 + 共享字段第一批
- [ ] 阶段 2: 剩余共享字段 + GC backend 搬入 VmCore
- [ ] 阶段 3: GcRef Arc backing + MagrGC trait Send/Sync
- [ ] 阶段 4: 调用方机械清理
- [ ] 阶段 5: 测试 + benchmark + 文档
- [ ] 阶段 6: 归档 + 提交

## 阶段 1: VmCore 雏形 + 第一批字段
- [x] 1.1 `src/runtime/src/vm_context.rs` —— 新增 `pub struct VmCore { ... }`（先空壳）+ `pub(crate) core: Arc<VmCore>` 字段
- [x] 1.2 移动 `static_fields` / `static_field_index` 到 VmCore（`Mutex<Vec<Value>>` / `Mutex<HashMap<String, u32>>`）
- [x] 1.3 调用方更新：所有 `self.static_fields.borrow()` → `self.core.static_fields.lock()`
- [x] 1.4 `cargo build --release` GREEN
- [x] 1.5 `./scripts/test-stdlib.sh` GREEN（17 lib）
- [x] 1.6 移动 `lazy_loader`（`Mutex<Option<LazyLoader>>`）+ 调用方更新
- [x] 1.7 移动 `native_types`（`RwLock<HashMap<...>>`，读多写少）+ 调用方
- [x] 1.8 移动 `native_libs`（`Mutex<Vec<libloading::Library>>`）+ 调用方
- [x] 1.9 移动 `pinned_owned_buffers`（`Mutex<HashMap<...>>`）+ 调用方
- [ ] 1.10 阶段 1 全 GREEN check（stdlib + test-vm + dotnet test）

## 阶段 2: 剩余共享字段
- [x] 2.1 移动 `processes`（`Mutex<HashMap<u64, ProcessSlot>>`）+ 调用方
- [ ] 2.2 移动 GC backend `gc: Arc<dyn MagrGC + Send + Sync>` 到 VmCore（先用 dyn box，arc 边界待阶段 3）
- [ ] 2.3 阶段 2 全 GREEN

## 阶段 3: GcRef + MagrGC trait（大块）
- [ ] 3.1 `src/runtime/src/gc/refs.rs`：`GcRef.inner: Rc<...>` → `Arc<...>`；`GcAllocation.inner: RefCell<T>` → `parking_lot::Mutex<T>`；`GcAllocation.finalizer` 同
- [ ] 3.2 `gc/refs.rs`：`pub type Ref<'a, T> = parking_lot::MutexGuard<'a, T>` + 同 RefMut；保留 `borrow()` / `borrow_mut()` API
- [ ] 3.3 `gc/refs.rs`：`borrow_mut` 内部用 `try_lock`，failure → panic with "recursive borrow_mut on GcRef" 信息
- [ ] 3.4 `src/runtime/src/gc/heap.rs`：`pub trait MagrGC: Debug + Send + Sync { ... }`
- [ ] 3.5 `src/runtime/src/gc/rc_heap.rs` rename → `arc_heap.rs`；`pub struct RcMagrGC` → `pub struct ArcMagrGC`；内部 `Rc<RefCell<HashMap>>` → `Arc<Mutex<HashMap>>`
- [ ] 3.6 `gc/mod.rs` re-export 改为 `ArcMagrGC`；Phase 表加新行 `4a: Send-safe foundation`
- [ ] 3.7 verify `Cargo.toml` 含 `parking_lot`；如缺加 `parking_lot = "0.12"`
- [ ] 3.8 阶段 3 全 GREEN（这一步 compile error 最多；按编译器提示逐一修）

## 阶段 4: 调用方机械清理
- [ ] 4.1 grep `\.borrow()` / `\.borrow_mut()` 残留：runtime 内剩下的都应该走 GcRef API
- [ ] 4.2 grep `Rc<RefCell<` runtime 内残留；除 VmContext per-thread 字段外应为 0
- [ ] 4.3 删除 unused `use std::rc::Rc;` / `use std::cell::RefCell;` import
- [ ] 4.4 验证 `cargo clippy` 不增警告

## 阶段 5: 测试 + benchmark + 文档
- [ ] 5.1 `src/runtime/src/gc/heap_tests.rs` 加 `assert_send_sync::<VmCore>()`、`assert_send_sync::<GcRef<Vec<Value>>>()`、`assert_send_sync::<Arc<dyn MagrGC>>()`
- [ ] 5.2 `src/runtime/tests/cross_thread_smoke.rs` 新建：构 VmCore，分配 GcRef，跨线程 read（不调 z42 函数）
- [ ] 5.3 `./scripts/test-all.sh` 全绿（含 stdlib + VM e2e + cross-zpkg）
- [ ] 5.4 手测 `time ./scripts/test-vm.sh` 前/后对比；记入"实施期发现"段
- [ ] 5.5 `docs/design/runtime/vm-architecture.md` 新章节 "VmCore / VmContext 分离"（数据结构 + per-thread vs shared 规则）
- [ ] 5.6 `docs/design/runtime/vm-architecture.md` "GC 子系统" 段加 Phase 4a 行
- [ ] 5.7 `docs/design/runtime/gc-handle.md` 更新 backing 描述（Arc + Mutex）
- [ ] 5.8 `docs/design/runtime/concurrency.md` 加 "runtime foundation 现状（add-multithreading-foundation 2026-05-19 落地）" 章节，链接本 archive
- [ ] 5.9 `docs/roadmap.md` Pipeline 实现进度表更新（如适用）

## 阶段 6: 归档 + 提交
- [ ] 6.1 mv → `docs/spec/archive/2026-05-19-add-multithreading-foundation/`
- [ ] 6.2 commit + push（每 phase 一个 commit；不囤积）
- [ ] 6.3 验证 GitHub Actions CI 全绿（macOS / Linux / Windows）

## 备注

（实施中发现写这里）

**Phase 1 实施发现（2026-05-19）**：

- 撞到 pre-existing failure：`zbc_version_constants_pinned` / `zpkg_version_constants_pinned` 两个 pin 测试卡在老 minor 值（5 / 6），但 `ZBC_VERSION_MINOR` / `ZPKG_VERSION_MINOR` 常量已 bump 到 6 / 7（并行 spec `fix-array-default-init` writer 改了，pin 测试漏跟）。
- 行动：作为 Phase 1 commit 顺手修了（`zbc_reader_tests.rs:118-126`），2 行机械变更。理由：pin 测试就是用来报警 version bump 漏修的，让它持续 RED 违反 GREEN 门禁。同时已记入此备注。

## 后续相关 spec（提前列出避免 scope 漂移）

| 名称 | 范围 |
|------|------|
| `add-threading-stdlib` | `Std.Threading.Thread.Start` / `.Join` / per-thread heap-local（用本 spec 的 foundation）|
| `add-sync-primitives` | `Std.Threading.Mutex<T>` / `Channel<T>`（用户类型）|
| `add-gc-safepoint` | 并发 GC 前置：interp / JIT 插入 safepoint，让 GC 能安全 stop-the-world |
| `add-concurrent-gc` | mark-sweep 升级到并发（Phase A 性能轨道）|
| `add-spawn-syntax` | `spawn` / `task scope` 语言层（L3，concurrency.md §3.5）|
