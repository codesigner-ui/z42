# Proposal: add-gc-softref

## Why

z42 currently has strong and weak references (`Std.WeakHandle` / `Std.GCHandle`).
Weak refs are cleared at the **first** GC cycle when no strong ref exists —
optimal for caches where stale-on-pressure is too aggressive. Users building
memory-sensitive caches (e.g., bytecode caches, parsed-template pools,
image thumbnails) need a middle tier: "keep as long as memory allows, discard
when the heap is stressed". Without soft refs they must manually poll
`GC.UsedBytes()` and evict, which is brittle and races with allocation.

## What Changes

- New `SoftGcRef<T>` at Rust level (analogous to `WeakGcRef<T>`)
- New `RegionEntry<T>.soft_ref_count: AtomicU32` — counts live soft refs to this entry
- Heap-internal soft-ref registry + post-mark revive pass
- New script class `Std.SoftHandle` with `Create(object)` / `Get() -> object`
- Two new corelib builtins: `__soft_handle_create` / `__soft_handle_get`
- `Z42_GC_SOFT_THRESHOLD` env var (default `0.80`) controlling pressure cutoff

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/refs.rs` | MODIFY | 新增 `SoftGcRef<T>` struct + `SoftGcRef::new` / `upgrade` / `Drop` |
| `src/runtime/src/gc/region.rs` | MODIFY | 新增 `soft_ref_count: AtomicU32` 字段 |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | soft-ref registry (`soft_refs: Mutex<Vec<ErasedSoftEntry>>`) + post-mark revive pass |
| `src/runtime/src/gc/heap.rs` | MODIFY | `MagrGC` trait 加 `register_soft_ref` / `soft_ref_get` 方法 |
| `src/runtime/src/corelib/gc.rs` | MODIFY | `builtin_soft_handle_create` / `builtin_soft_handle_get` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册两个新 builtin |
| `src/libraries/z42.core/src/GC/SoftHandle.z42` | NEW | `Std.SoftHandle` 脚本类 |
| `src/libraries/z42.core/z42.core.z42.toml` | MODIFY | 注册 `SoftHandle.z42` |
| `src/tests/gc/gc_softhandle_basic/` | NEW | 基础功能 golden test |
| `src/tests/gc/gc_softhandle_pressure/` | NEW | 压力回收 golden test |
| `docs/design/runtime/gc.md` | MODIFY | B2 标记 ✅，加 Phase 表行，加 Deferred 条目 |

**只读引用：**
- `src/runtime/src/gc/arc_heap.rs` mark_phase / sweep_phase — 确认 revive pass 插入点
- `src/libraries/z42.core/src/GC/WeakHandle.z42` — 参考脚本 API 模式
- `src/runtime/src/corelib/gc.rs` — 参考 weak-ref builtin 实现

## Out of Scope

- JIT helper `jit_obj_new` / `jit_array_new` 不注入 soft-ref 回收（与 gc-oom-jit-path 延后一致）
- `SoftRef<T>` 泛型语法（L2 泛型特性未就位）— 用 `SoftHandle` 非泛型类替代
- Per-generation soft-ref 策略（old-gen-only soft 回收）— 延后
- `GCHandle.AllocSoft` 重载（与 `SoftHandle` 并列 API）— 延后

## Open Questions

- [x] 压力阈值：内置 0.80（= 80% `max_heap_bytes` 使用）；`max_heap_bytes == 0`（无限堆）时软引用永不回收 — 已确认
- [x] 线程安全：`soft_refs` 用 `Mutex<Vec<...>>`，与 `mark_phase` 持有 heap lock 一致 — 已确认
