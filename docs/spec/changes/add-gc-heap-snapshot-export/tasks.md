# Tasks: GC Heap Snapshot Export (B3)

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-24 | 类型：vm

**总体策略**：one new module `gc/snapshot.rs` + 1 new builtin +
1 z42 declaration + tests. Pure observability addition. No GC
algorithm change.

**总工作量估算**：~1-2 sessions / ~450 LOC.

## 进度概览

- [ ] 阶段 1-6: spec 文档
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0
- [ ] 阶段 8: GREEN
- [ ] 阶段 9: 归档

## P0: GraphSnapshot + builder + V8 serializer + builtin (~1 session)

- [ ] P0.1 NEW `src/runtime/src/gc/snapshot.rs`:
       - `NodeType` / `EdgeType` enums (`#[repr(u8)]`)
       - `NodeRec` / `EdgeRec` / `GraphSnapshot` types
       - `GraphSnapshot::new()` (constructor with synth root node id=0)
       - `intern_node`, `intern_str`, `push_edge` helpers
       - `node_index_by_id` for V8 `to_node` offset compute
       - `build_graph_snapshot(heap: &dyn MagrGC) -> GraphSnapshot`
       - `serialize_v8_heapsnapshot(&GraphSnapshot) -> String`
       - `value_ptr(&Value) -> Option<usize>` helper for dedup keying
- [ ] P0.2 NEW `src/runtime/src/gc/snapshot_tests.rs` (7+ unit tests):
       - `empty_heap_produces_root_only`
       - `single_object_creates_node_with_property_edges`
       - `linked_objects_emit_property_edges`
       - `array_emits_element_edges_with_index`
       - `cycle_does_not_loop`
       - `node_ids_are_odd_except_root`
       - `serialized_json_parses_and_has_expected_structure`
- [ ] P0.3 MODIFY `src/runtime/src/gc/mod.rs` — `pub mod snapshot;`
       + 重新导出 `GraphSnapshot` 顶层
- [ ] P0.4 MODIFY `src/runtime/src/corelib/gc.rs` — add
       `builtin_gc_write_heap_snapshot(ctx, args)`：parse string path,
       call builder + serializer, write file, return bytes as i64
- [ ] P0.5 MODIFY `src/runtime/src/corelib/mod.rs` — append
       `("__gc_write_heap_snapshot", gc::builtin_gc_write_heap_snapshot)`
       at the end (preserve existing BuiltinIds, per resource-loading
       deterministic-order convention)
- [ ] P0.6 MODIFY `src/libraries/z42.core/src/GC/GC.z42` — add
       `extern long WriteHeapSnapshot(string path)` with doc-comment
       linking to V8 .heapsnapshot format + Chrome DevTools
- [ ] P0.7 NEW `src/libraries/z42.core/tests/gc_heap_snapshot.z42` —
       end-to-end test: alloc + WriteHeapSnapshot + verify file
       exists + parse-as-JSON sanity + cleanup
- [ ] P0.8 `cargo --lib gc::` GREEN（含新 7+ unit tests）
- [ ] P0.9 `test-all.sh --scope=full` GREEN
- [ ] P0.10 commit

## P1: gc.md docs + archive (~0.5 session)

- [ ] P1.1 MODIFY `docs/design/runtime/gc.md`：
       - "Heap snapshot export" new subsection (right after
         "Pause histogram"):
         - V8 .heapsnapshot format brief; link DevTools docs
         - Node + edge type mapping table
         - Script-side usage example
         - 局限 (no alloc trace v1, no streaming, no weak edges)
       - Phase 路线表 add B3 row
       - B3 backlog entry "future → landed"
- [ ] P1.2 archive 到 `docs/spec/archive/YYYY-MM-DD-add-gc-heap-snapshot-export/`
- [ ] P1.3 final `test-all.sh --scope=full` GREEN
- [ ] P1.4 commit + push

## 备注

实施期发现入 commit message + 备注 section.

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-gc-snapshot-alloc-trace` | trace_function_infos / trace_tree (需要 IR alloc_site_id, depends on B4) |
| `add-gc-snapshot-streaming` | 大堆 streaming serializer 减峰值内存 |
| `add-gc-snapshot-retainer-dominator` | server-side dominator tree 预算 |
| `add-gc-snapshot-weak-edges` | EdgeType::Weak 表示 WeakGcRef |
