# Tasks: GC Heap Snapshot — Streaming Serializer

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-24 | 类型：vm

**总体策略**：retrofit `serialize_v8_heapsnapshot` 为薄包装；新加
`serialize_v8_heapsnapshot_to<W: Write>` 流式直写；builtin 改用
`BufWriter<File>`. Pure perf, byte-identical output, no behaviour
change.

**总工作量估算**：~0.5-1 session / ~150 LOC.

## 进度概览

- [ ] 阶段 1-6: spec 文档
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0
- [ ] 阶段 8: GREEN
- [ ] 阶段 9: 归档

## P0: streaming serializer + builtin wire-up (~0.5 session)

- [ ] P0.1 MODIFY `src/runtime/src/gc/snapshot.rs`:
       - Add `serialize_v8_heapsnapshot_to<W: Write>(snap, writer) ->
         io::Result<u64>` (full layout, byte-counted)
       - Add `escape_json_str_to<W: Write>(s, writer) -> io::Result<u64>`
       - Refactor existing `serialize_v8_heapsnapshot` → calls
         streaming serializer driven by `Vec<u8>`
       - Drop `escape_json_str(&str, &mut String)` (no callers after
         refactor)
- [ ] P0.2 MODIFY `src/runtime/src/gc/snapshot_tests.rs`:
       - Add `streaming_and_in_memory_produce_identical_bytes`
       - Add `streaming_byte_count_matches_string_length`
       - Add `streaming_writes_to_buffered_writer_roundtrip` (tempfile)
- [ ] P0.3 MODIFY `src/runtime/src/corelib/gc.rs`:
       - `builtin_gc_write_heap_snapshot` 改用 `File::create` +
         `BufWriter::new` + `serialize_v8_heapsnapshot_to` + 显式
         `flush()`
- [ ] P0.4 `cargo --lib gc::` GREEN（含新 3 unit tests）
- [ ] P0.5 `test-all.sh --scope=full` GREEN
- [ ] P0.6 Commit P0

## P1: gc.md docs + archive

- [ ] P1.1 MODIFY `docs/design/runtime/gc.md`:
       - "Heap snapshot export" 段加 streaming 注解（v1 用 `BufWriter<File>`
         消除中间 `String`）
       - Phase 路线表加行 add-gc-snapshot-streaming
       - B3 (heap-snapshot-export) Deferred sub-list flip
         `add-gc-snapshot-streaming` future → landed
- [ ] P1.2 Archive 到 `docs/spec/archive/2026-05-24-add-gc-snapshot-streaming/`
- [ ] P1.3 Final `test-all.sh --scope=full` GREEN
- [ ] P1.4 Commit + push

## 备注

实施期发现入 commit message + 备注 section.
