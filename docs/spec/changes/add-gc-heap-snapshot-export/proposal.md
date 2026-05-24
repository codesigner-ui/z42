# Proposal: GC Heap Snapshot Export (V8 / DevTools format)

## Why

z42 has all the GC introspection plumbing (`MagrGC::iterate_live_objects`,
`scan_object_refs`, `object_size_bytes`, `take_snapshot` aggregate
summary) but no way for a script / embedder to **dump the object
graph** to a file that opens in existing tools (Chrome DevTools
Memory tab / `perfetto.dev/viewer` / `speedscope` / etc).

Today: "z42 process is using 800 MB, what's holding it?" is answered
by reading per-class aggregates in `Std.GC.GetStats()` — useful but
coarse. No way to see the **retainer graph**, dominator tree, or
shortest-path-to-root for a specific object. Every other modern
runtime (V8, .NET, JVM, CPython 3.13) exports a heap snapshot file.

This proposal adds **one builtin** `Std.GC.WriteHeapSnapshot(path)`
that walks the heap once and writes a V8 `.heapsnapshot` JSON file
loadable directly in Chrome DevTools → Memory → Load.

Pure observability addition. No new GC algorithm, no behavior change
to alloc / collect paths. Zero impact when not invoked.

## What Changes

- New type `GraphSnapshot` in `gc/snapshot.rs`: V8-shaped record of
  `{ nodes: Vec<NodeRec>, edges: Vec<EdgeRec>, strings: Vec<String> }`
- New host-side function `build_graph_snapshot(heap: &dyn MagrGC) ->
  GraphSnapshot` that walks live objects + scans children
- New V8 JSON serializer `serialize_v8_heapsnapshot(&GraphSnapshot)
  -> String` matching the canonical layout (meta + flat node array +
  flat edge array + string table)
- New z42 builtin `__gc_write_heap_snapshot(path)` that takes the
  snapshot, serializes it, writes to disk; returns bytes written
- New `Std.GC.WriteHeapSnapshot(path: string): long` z42-script API
  + doc comment

Default behavior unchanged: the snapshot builder + serializer are
only entered when the new builtin is called.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/snapshot.rs` | NEW | `GraphSnapshot` 类型 + builder (walk via `iterate_live_objects` + `scan_object_refs`) + V8 JSON serializer |
| `src/runtime/src/gc/snapshot_tests.rs` | NEW | 单测：known heap shape → snapshot 包含 expected nodes / edges / strings 表（不去对完整 JSON byte-equal，只验 invariants） |
| `src/runtime/src/gc/mod.rs` | MODIFY | `pub mod snapshot;` + 重新导出 `GraphSnapshot` |
| `src/runtime/src/corelib/gc.rs` | MODIFY | `builtin_gc_write_heap_snapshot(ctx, args)` — 接 path 参数；调 `gc::snapshot::build_graph_snapshot` + `serialize_v8_heapsnapshot` + `std::fs::write`；返 bytes written 为 i64 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__gc_write_heap_snapshot`（append 末尾，preserve BuiltinIds） |
| `src/libraries/z42.core/src/GC/GC.z42` | MODIFY | `extern long WriteHeapSnapshot(string path)` 声明 + 文档化 V8 format / Chrome DevTools 加载入口 |
| `src/libraries/z42.core/tests/gc_heap_snapshot.z42` | NEW | 端到端：alloc + Std.GC.WriteHeapSnapshot(tmp.heapsnapshot) + Std.IO.File.Exists 校验 + file size > 0 |
| `docs/design/runtime/gc.md` | MODIFY | "Heap snapshot export" 新 subsection；B3 backlog "future → landed"；Phase 表加 B3 行 |

**只读引用**：

- `src/runtime/src/gc/heap.rs` — `MagrGC` trait 已有的
  `iterate_live_objects` / `scan_object_refs` / `object_size_bytes`
- `src/runtime/src/gc/types.rs` — `HeapSnapshot` (老 aggregate
  snapshot 不动；新 `GraphSnapshot` 独立类型)
- `src/runtime/src/metadata/value.rs` — `Value` 形状 + `trace_children`
- `src/runtime/src/metadata/type_desc.rs` — TypeDesc.name / field 命名
- V8 .heapsnapshot 格式参考：
  - <https://chromedevtools.github.io/devtools-protocol/tot/HeapProfiler/>
  - V8 源码 `src/heap-snapshot-generator.cc`（meta layout）

## Out of Scope

- **Trace function infos / allocation tracking**：V8 格式支持的
  "alloc site stack trace" 在 z42 暂没 IR site ID（这是 B4 backlog
  spec 的范围）。v1 输出空 `trace_function_infos`、`trace_tree`、
  `samples`、`locations` 数组 — DevTools 能正常 load，"Retainers"
  tab 仍可用
- **Per-object retainer dominator pre-computation**：DevTools 自己算
  dominator tree；我们只输出 raw graph
- **String interning across snapshots**：每次 snapshot 独立一份 string
  table，不跨调用复用
- **大堆增量序列化 / streaming**：v1 build full snapshot in memory →
  one `std::fs::write` call。多 GB heap 用户可手动 GC.ForceCollect 减
  噪后调；真正 streaming 留给后续 perf spec
- **WeakRef edges in graph**：weak refs 不构成 retention，跳过；只
  trace strong refs (与 mark phase 一致)
- **Cross-VmCore snapshot**：只 dump 当前 ctx.heap()；多 VmCore 嵌
  入用户分别调用

## Open Questions

无（V8 格式 well-specified；z42 节点类型映射在 design.md 里覆盖）。
