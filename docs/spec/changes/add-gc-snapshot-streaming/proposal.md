# Proposal: GC Heap Snapshot — Streaming Serializer

## Why

B3 (add-gc-heap-snapshot-export) `serialize_v8_heapsnapshot(snap) ->
String` builds the **entire JSON output as one in-memory `String`**
before writing to disk. For a 10 MB live heap this peaks at ~30 MB
of additional allocation — fine. For a multi-GB live heap, that
intermediate String roughly doubles the GraphSnapshot's own memory
footprint right when the process is already memory-stressed (the
typical reason someone reaches for a heap snapshot).

A streaming serializer that writes JSON tokens directly to an
`impl Write` sink (typically `BufWriter<File>`) halves the peak.
The intermediate String goes away; the `GraphSnapshot` itself still
lives in memory (its node + edge arrays are O(N+E) but each entry
is ~32-bytes packed — much smaller than the corresponding JSON
string), but is the minimum-possible cost.

Pure perf addition. Zero algorithmic change to the snapshot walker.
Output bytes are byte-identical to what the in-memory serializer
produced (we keep the same JSON layout, same field order, same
escaping rules).

## What Changes

- New function `serialize_v8_heapsnapshot_to<W: Write>(snap:
  &GraphSnapshot, writer: &mut W) -> std::io::Result<u64>` in
  `gc/snapshot.rs` — writes JSON directly to `writer`, returns
  bytes written
- Existing `serialize_v8_heapsnapshot(&GraphSnapshot) -> String`
  retained as a thin wrapper around the streaming variant (drives
  a `Vec<u8>` `Write` sink + `String::from_utf8_unchecked`).
  Existing callers + unit tests untouched
- `builtin_gc_write_heap_snapshot` now opens `BufWriter<File>` and
  calls the streaming serializer directly, bypassing the String
  intermediate
- New invariant test: streaming + in-memory paths produce
  byte-identical output for a fixed `GraphSnapshot`

Default behavior unchanged. Memory peak per `WriteHeapSnapshot`
roughly halved on large heaps.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/snapshot.rs` | MODIFY | 加 `serialize_v8_heapsnapshot_to<W: Write>(snap, &mut writer) -> io::Result<u64>`；现有 `serialize_v8_heapsnapshot` 改为驱动 `Vec<u8>` 的薄包装；抽取 `META_HEADER` 写入复用；`escape_json_str` 接 `&mut W` 替代 `&mut String` |
| `src/runtime/src/gc/snapshot_tests.rs` | MODIFY | 加 `streaming_and_in_memory_produce_identical_bytes` + `streaming_writes_bytes_match_in_memory_length` |
| `src/runtime/src/corelib/gc.rs` | MODIFY | `builtin_gc_write_heap_snapshot` 改用 `BufWriter<File>` + 调 streaming serializer；返 bytes_written 仍是 i64 |
| `docs/design/runtime/gc.md` | MODIFY | "Heap snapshot export" 段加 streaming 注解 + Phase 表加行 + B3 Deferred sub-list 把 `add-gc-snapshot-streaming` 标 landed |

**只读引用**：

- B3 archive (`docs/spec/archive/2026-05-24-add-gc-heap-snapshot-export/`)
- 现有 GraphSnapshot 形状 + JSON 布局

## Out of Scope

- **Walker streaming**：true interleave (walk → emit → drop) 需要
  rework `build_graph_snapshot` 让节点 / 边产出后即可释放。但
  V8 格式需要 `node_count` + `edge_count` 在 `snapshot.meta` 段
  pre-declared，意味着 walker 至少要先跑一次 size 计算或两次
  walk。复杂度上不值得 v1 — `add-gc-snapshot-walker-streaming`
  独立 spec。
- **写入 fsync / 错误恢复**：依赖 `std::io::Write` 的契约；调用方
  传 `BufWriter<File>`，flush + Drop close 由 caller 管。
- **进度回调 / interrupt**：长 snapshot 期间脚本 / 嵌入用户能否
  cancel — 留给后续 ergonomics spec。
- **gzip 压缩**：V8 .heapsnapshot 默认未压缩；用户自己用 host gzip
  压缩，z42 不做 transparent 压缩 v1。
- **Symbolic snapshot output（trace_function_infos）**：依赖 B4 IR
  alloc_site_id。

## Open Questions

无。
