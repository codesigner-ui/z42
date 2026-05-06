# Proposal: Reorganize GC stdlib + Add Unified GCHandle

## Why

z42 stdlib 当前 GC 相关接口"散在 Delegates/"且功能单薄：

- `src/libraries/z42.core/src/GC.z42` 仅暴露 3 个静态方法（Collect / UsedBytes /
  ForceCollect），未对接 Rust heap 层早已就绪的 HeapStats 全套字段
- `src/libraries/z42.core/src/Delegates/WeakHandle.z42` 因 D-1a 是"D-1b WeakRef
  wrapper 的原料"才落到 Delegates/，主题归属错位
- 用户脚本无法用 C# 风格 `GCHandle` 统一管理弱/强引用 + 显式 `Free`
  （C# `System.Runtime.InteropServices.GCHandle` 是 1 字段 struct + CLR
  handle table backing，z42 缺等价 primitive）

## What Changes

1. 新建 `src/libraries/z42.core/src/GC/` 目录，搬入 `GC.z42` + `WeakHandle.z42`
2. 新增 `Std.GCHandle` —— **struct**，单字段 `_slot: long`，背后是 corelib
   `HandleTable`（slab + free list）；拷贝句柄共享同一 slot；Free 后所有
   alias 同步失效（与 C# `GCHandle` struct 语义一致）
3. 新增 `Std.GCHandleType` 枚举（Weak / Strong；Pinned / WeakTrackResurrection
   待 Phase 3 tracing GC）
4. 新增 `Std.HeapStats` 类（7 字段映射 Rust `HeapStats`；MaxBytes 用 -1 sentinel
   表示 unlimited，z42 当前无 Optional<T>）+ `Std.GC.GetStats()` 静态方法
5. 保留 `Std.WeakHandle` 作为轻量 primitive（无 Free，自动 GC；GCHandle 是双模
   显式控制 wrapper，二者并存如 C# `WeakReference` 与 `GCHandle`）
6. Rust 端：`MagrGC` trait 加 `HandleTable` API；`RcMagrGC` 实现双模 slab；
   `corelib/gc.rs` 加 5 个 GCHandle builtin + 1 个 stats builtin

## Scope（允许改动的文件）

| 文件 | 变更 |
|------|------|
| `src/libraries/z42.core/src/GC/GCHandle.z42` | NEW struct + GCHandleType enum |
| `src/libraries/z42.core/src/GC/HeapStats.z42` | NEW class（7 字段）|
| `src/libraries/z42.core/src/GC/README.md` | NEW 子目录 README |
| `src/libraries/z42.core/src/GC/GC.z42` | RENAME from `src/libraries/z42.core/src/GC.z42`；增加 `GetStats()` 方法 |
| `src/libraries/z42.core/src/GC/WeakHandle.z42` | RENAME from `src/libraries/z42.core/src/Delegates/WeakHandle.z42`；内容不变 |
| `src/libraries/z42.core/src/README.md` | MODIFY 目录表 + 跨目录依赖表 |
| `src/runtime/src/gc/types.rs` | MODIFY 加 `GcHandleSlot` / `GcHandleKind` 类型 |
| `src/runtime/src/gc/heap.rs` | MODIFY 加 HandleTable trait API（alloc / target / kind / is_alloc / free）|
| `src/runtime/src/gc/rc_heap.rs` | MODIFY 实现 HandleTable（slab + free list）|
| `src/runtime/src/gc/rc_heap_tests.rs` | MODIFY 加 HandleTable 单元测试 |
| `src/runtime/src/corelib/gc.rs` | MODIFY 加 5 个 GCHandle builtin + 1 个 HeapStats builtin |
| `src/runtime/src/corelib/mod.rs` | MODIFY dispatch table 加 6 行 |
| `src/runtime/tests/golden/run/111_gc_handle/` | NEW golden test（strong + weak 端到端）|
| `src/runtime/tests/golden/run/112_gc_stats/` | NEW golden test（HeapStats 字段一致性）|
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY z42.core 文件数（+2 .z42 文件）|
| `docs/design/stdlib.md` | MODIFY z42.core 目录树 + 加 GCHandle 章节 |
| `docs/design/gc-handle.md` | NEW 实现原理文档（HandleTable 数据结构、Strong/Weak 语义、Phase 3 扩展规划）|
| `docs/roadmap.md` | MODIFY 加本变更条目 |

**只读引用**：

- `src/runtime/src/runtime/value.rs` —— 理解 `Value::Object/Array` 与 `Rc::clone` 关系
- `src/libraries/z42.core/src/Delegates/SubscriptionRefs.z42` / `DelegateOps.z42`
  —— 现有 `WeakHandle` 内部消费者；本变更**不**改动它们的代码（仅 import 路径
  随文件移动自动跟随，z42 namespace 内可见性不依赖物理路径）
- `spec/archive/2026-04-29-expose-gc-to-scripts/tasks.md` —— 既有 GC.z42 builtin pattern
- `spec/archive/2026-05-04-expose-weak-ref-builtin/` —— WeakHandle primitive 现状

## Out of Scope（明确推后）

- `GCHandleType.Pinned` —— Phase 1 RC 不移动对象，无 use case；Phase 3 tracing GC 引入
- `GCHandleType.WeakTrackResurrection` —— 依赖 finalizer 触发，Phase 1 RC 不支持
- `GC.Pause()` / `GC.Resume()` / `GC.MaxHeapBytes` —— GC 控制面，下一个 spec
- `GC.AddObserver` / `GC.TakeSnapshot` —— 跨边界回调与 heavy snapshot，Phase 3+ 评估
- `GC.RegisterFinalizer` —— Phase 1 RC 模式注册不触发，避免误导用户
- C# `GCHandle.AddrOfPinnedObject()` / IntPtr 互转 —— Pinned 未引入

## Open Questions

- [x] **Q1**：GCHandle 选 struct + corelib handle table backing → ✅
- [x] **Q2**：保留 WeakHandle 作为轻量 primitive（不删）→ ✅
- [x] **Q3**：Strong slot 语义 = `Rc::clone(target)`，anchor 直到 Free → ✅
- [x] **Q4**：`HeapStats.MaxBytes = -1` 表示 unlimited（z42 无 `Optional<T>`）→ ✅
- [x] **Q5**：struct 全链路风险作为 tasks.md 第一项验证（不单独 spike）→ ✅
