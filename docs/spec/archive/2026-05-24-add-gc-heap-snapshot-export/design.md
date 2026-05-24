# Design: GC Heap Snapshot Export

## Architecture

```
 z42 script
   ↓
 Std.GC.WriteHeapSnapshot(path)
   ↓
 corelib/gc.rs::builtin_gc_write_heap_snapshot
   ↓
 ┌─────────────────────────────────────────────┐
 │ gc/snapshot.rs                              │
 │                                             │
 │  build_graph_snapshot(&dyn MagrGC)          │
 │   ├─ pass 1: iterate_live_objects → assign  │
 │   │   sequential node id, build node recs   │
 │   ├─ pass 2: for each node, scan_object_refs│
 │   │   → resolve child node id, push edge    │
 │   └─ pass 3: synthesize root node + edges   │
 │                                             │
 │  serialize_v8_heapsnapshot(&GraphSnapshot)  │
 │   → String (JSON, V8 layout)                │
 └─────────────────────────────────────────────┘
   ↓
 std::fs::write(path, json_bytes)
   ↓
 returns i64 bytes written
```

## Decisions

### Decision 1: V8 `.heapsnapshot` format (not custom)

**问题**：自定义格式 vs 既有工具兼容格式？

**选项**：
- A — z42 自定义 JSON schema：简单；但用户需手写 viewer
- B — V8 `.heapsnapshot`：~200 LOC 多写一些 layout 代码；但
  Chrome DevTools / VSCode / [speedscope](https://www.speedscope.app)
  / [heapviewer.com](https://heapviewer.com) 全部直接 load
- C — `.hprof` (Java)：JVM 生态；z42 不在 JVM 用户群

**决定**：B. 投入很小（~200 LOC layout）换全部业界 viewer 直接可
用，ROI 无对手。

### Decision 2: Two-pass build (id assignment first, then edges)

**问题**：怎么 emit 边？需要先有节点 id 才能填 `to_node`。

**选项**：
- A — Single pass：iterate 时直接 emit node + edge；edge 指向"未来
  会出现的 id"需要回填
- B — Two pass：pass 1 assign ids + emit nodes，pass 2 emit edges
  (id 已知)
- C — Trampoline / closure with mutable map

**决定**：B. 一次性 walk 是 O(N); two-pass 是 O(N) + O(N+E) = O(N+E)
（同 big-O，常数翻 2）。代码大幅简化（无回填，无 Option 占位），
对 multi-GB heap 仍是几秒级。`HashMap<*const RegionEntry<_>, u32>`
按 pointer key 做 id 表（基于 Region 的 NonNull 是 stable）。

### Decision 3: Node type mapping

V8 节点 type 是 `[hidden, array, string, object, code, closure,
regexp, number, native, synthetic, concatenated string, sliced
string, symbol, bigint]`. z42 mapping：

| z42 Value variant | V8 node type | name 字段 |
|-------------------|--------------|-----------|
| `Value::Object(gc)` | `object` (3) | `TypeDesc.name` |
| `Value::Array(gc)` | `array` (1) | `"Array[{len}]"` |
| Root pseudo-node | `synthetic` (9) | `"(GC roots)"` |

Primitive Values (`I64`/`F64`/`Bool`/`Char`/`Str`/`Null`) 不是堆节
点；它们出现在 edge `name_or_index` 端或干脆 skip（V8 也不为
non-heap primitives 建 node）。Atomic `Value::Str` 在 z42 当前是
inlined string，不是 heap-allocated → 不出现在 graph。

### Decision 4: Edge type mapping

V8 edge type 是 `[context, element, property, internal, hidden, shortcut, weak]`. z42 mapping：

| 来源 | V8 edge type | name_or_index |
|------|--------------|----------------|
| `ScriptObject.slots[i]` 字段 | `property` (2) | string id of `TypeDesc.fields[i].name` |
| `Vec<Value>[i]` 数组元素 | `element` (1) | index `i` |
| GC roots → object | `shortcut` (5) | empty string |

Slot 命名 fallback：若 `TypeDesc.fields[i].name` 缺失（应该不会，
但保险），用 `"slot{i}"`.

### Decision 5: Root node synthesis

**问题**：V8 snapshot 有一个 synthetic root node id=0 (`(GC roots)`)
节点。z42 没有单一 root 概念。

**决定**：合成一个 id=0 root node，其出边指向所有 pinned root
+ external scanner-emitted root 各一条 `shortcut` 边。这让 DevTools
"Retainers" tab 工作（每个对象能 trace back 到 root）。

### Decision 6: Strings: dedupe via HashMap

V8 格式 strings 是 flat array，所有 node name / edge name 字段都
是 string-table 索引。`HashMap<String, u32>` dedup 内存几乎免费
(typical heap 类名数 << 对象数)。

### Decision 7: Coverage = Full (current `iterate_live_objects`)

`MagrGC::iterate_live_objects` 已经 Full coverage (Phase 3b
add-heap-registry 起 + A1 region store 之后)。不需要"Reachable
from pinned roots" 模式 —— "Full"是更有用的视图。

### Decision 8: Build in memory, single fs::write

不引入 streaming serializer。每次调用 v1 build full graph in memory
→ JSON to String → 一次写。10MB heap → ~30 MB snapshot file (V8
的 char-count overhead)。可接受。优化是后续 perf spec。

## Implementation Notes

### GraphSnapshot struct

```rust
// gc/snapshot.rs
pub struct GraphSnapshot {
    pub nodes:   Vec<NodeRec>,
    pub edges:   Vec<EdgeRec>,
    pub strings: Vec<String>,
    pub root_node_id: u32,  // always 0 in v1
}

pub struct NodeRec {
    pub node_type: NodeType,   // u8 enum repr
    pub name_idx:  u32,        // index into strings
    pub id:        u32,        // V8 ids are odd (so script-side `==` checks
                               // work); we use 2k+1 sequence
    pub self_size: u32,        // bytes
    pub edge_count: u32,
    pub trace_node_id: u32,    // always 0 in v1
}

pub struct EdgeRec {
    pub edge_type:      EdgeType,
    pub name_or_index:  u32,   // string idx for property / numeric for element
    pub to_node_offset: u32,   // V8 stores as offset into nodes array =
                                // (to_node_id - 1) * NODE_FIELDS, computed
                                // at serialize time
}

#[repr(u8)]
pub enum NodeType {
    Object = 3, Array = 1, Synthetic = 9,
}

#[repr(u8)]
pub enum EdgeType {
    Element = 1, Property = 2, Shortcut = 5,
}
```

### build_graph_snapshot

```rust
pub fn build_graph_snapshot(heap: &dyn MagrGC) -> GraphSnapshot {
    let mut snap = GraphSnapshot::new();
    let mut id_map: HashMap<usize, u32> = HashMap::new(); // ptr → id

    // Synth root.
    let root_id = snap.intern_node(NodeType::Synthetic, "(GC roots)", 0, 0);

    // Pass 1: id assignment + node records.
    let mut all = Vec::new();
    heap.iterate_live_objects(&mut |v| all.push(v.clone()));
    for v in &all {
        let ptr = value_ptr(v);
        if id_map.contains_key(&ptr) { continue; }
        let (ty, name, size) = node_desc(v, heap);
        let id = snap.intern_node(ty, &name, size as u32, 0);
        id_map.insert(ptr, id);
    }

    // Pass 2: edges.
    for v in &all {
        let from_id = id_map[&value_ptr(v)];
        let from_idx = snap.node_index_by_id(from_id);
        heap.scan_object_refs(v, &mut |child| {
            if let Some(&to_id) = id_map.get(&value_ptr(child)) {
                let edge = edge_desc(v, child, &id_map);  // type + name_or_idx
                snap.push_edge(from_idx, edge.0, edge.1, to_id);
            }
        });
    }

    // Pass 3: root → pinned + external roots (shortcut edges).
    heap.for_each_root(&mut |v| {
        if let Some(&to_id) = id_map.get(&value_ptr(v)) {
            snap.push_edge(0, EdgeType::Shortcut, snap.intern_str(""), to_id);
        }
    });

    snap
}
```

### V8 JSON layout

```json
{
  "snapshot": {
    "meta": {
      "node_fields": ["type", "name", "id", "self_size", "edge_count", "trace_node_id", "detachedness"],
      "node_types": [["hidden","array","string","object","code","closure","regexp","number","native","synthetic","concatenated string","sliced string","symbol","bigint"], "string", "number", "number", "number", "number", "number"],
      "edge_fields": ["type", "name_or_index", "to_node"],
      "edge_types": [["context","element","property","internal","hidden","shortcut","weak"], "string_or_number", "node"],
      "trace_function_info_fields": ["function_id","name","script_name","script_id","line","column"],
      "trace_node_fields": ["id","function_info_index","count","size","children"],
      "sample_fields": ["timestamp_us","last_assigned_id"],
      "location_fields": ["object_index","script_id","line","column"]
    },
    "node_count": N,
    "edge_count": M,
    "trace_function_count": 0
  },
  "nodes": [3,0,1,128,2,0,0,  3,2,3,64,1,0,0, ...],
  "edges": [2,4,7,  1,0,14, ...],
  "trace_function_infos": [],
  "trace_tree": [],
  "samples": [],
  "locations": [],
  "strings": ["(GC roots)","Foo","slot0",...]
}
```

每 node 占 7 个 int (matching `node_fields`); 每 edge 占 3 个 int
(matching `edge_fields`). V8 `to_node` 是 byte-offset 进 nodes array:
`to_node = (id - 1) * 7`（root id=0 stays 0). z42 用 `id` 直接编（V8
spec 允许 `node` type 字段值是 node 数组中 offset，按 `node_fields`
count 倍数）。

### v8 字段编码注意点

V8 期望 `id` 是 **odd** 数字（其工具按"id % 2 == 1 → script-side"
判断）。我们用 `2 * sequential + 1`（root id=0 例外，被 V8 接受
作为合成节点）。

### Builtin

```rust
pub fn builtin_gc_write_heap_snapshot(ctx: &VmContext, args: &[Value])
    -> Result<Value>
{
    let path = match args.first() {
        Some(Value::Str(s)) => s.clone(),
        _ => return Err(anyhow!("__gc_write_heap_snapshot: expected string path")),
    };
    let snap = gc::snapshot::build_graph_snapshot(ctx.heap());
    let json = gc::snapshot::serialize_v8_heapsnapshot(&snap);
    let n = json.len();
    std::fs::write(&path, &json)
        .map_err(|e| anyhow!("__gc_write_heap_snapshot: write {}: {}", path, e))?;
    Ok(Value::I64(n as i64))
}
```

## Testing Strategy

### Unit tests (P0)

In `gc/snapshot_tests.rs`:

- `empty_heap_produces_root_only` — 0 user objects → 1 synthetic root
  node, 0 edges, strings = `["(GC roots)"]`
- `single_object_creates_node_with_property_edges` — 1 object with
  3 null slots → 2 nodes (root + obj), edges from root (shortcut to
  obj), no inter-object edges (nulls aren't heap refs)
- `linked_objects_emit_property_edges` — a.next = b → 3 nodes, edge
  a→b with EdgeType::Property
- `array_emits_element_edges_with_index` — array [obj1, obj2] → edges
  carry index 0/1 (not string name)
- `cycle_does_not_loop` — a→b→a → exactly 2 edges, no infinite loop
- `node_ids_are_odd_except_root` — V8 convention
- `serialized_json_parses_and_has_expected_structure` — parse via
  `serde_json::from_str` + assert meta + nodes/edges array lengths
  match counts

### Integration test (P0)

End-to-end stdlib test `gc_heap_snapshot.z42`:

```z42
[Test]
void test_write_snapshot_to_tmp_then_file_exists_and_nonempty() {
    string path = "/tmp/z42_test_snap.heapsnapshot";
    long bytes = GC.WriteHeapSnapshot(path);
    Assert.True(bytes > 100, "snapshot should not be trivially empty");
    // Read back; verify it parses as JSON.
    string content = File.ReadText(path);
    Assert.Contains("\"snapshot\"", content);
    Assert.Contains("\"nodes\"", content);
    Assert.Contains("\"edges\"", content);
    Assert.Contains("\"strings\"", content);
    Assert.Contains("(GC roots)", content);
    // Cleanup.
    File.Delete(path);
}
```

### Manual validation (off-CI)

Drop the file into Chrome DevTools → Memory → Load. Confirm it parses
and shows the synthetic root + nodes. (One-shot dev validation, not
automated — Chromium load logic is not on our test path.)

## Phasing

- **P0**: `gc/snapshot.rs` (~250 LOC) + `gc/snapshot_tests.rs` (~150
  LOC, 7 unit tests) + builtin + Std.GC.z42 declaration + 1 stdlib
  end-to-end test
- **P1**: gc.md "Heap snapshot export" subsection + Phase route table
  row + B3 backlog "future → landed" + archive

2 commits, ~1-2 sessions total.

## Deferred / Future Work

### add-gc-snapshot-alloc-trace

- Wire `trace_function_infos` / `trace_tree` / `samples` arrays once
  IR has per-callsite `alloc_site_id` (B4 backlog spec)

### add-gc-snapshot-streaming

- Streaming JSON serializer for multi-GB heap → constant memory dump

### add-gc-snapshot-retainer-dominator

- Pre-compute dominator tree server-side (faster DevTools load)

### add-gc-snapshot-weak-edges

- Emit `EdgeType::Weak` for `WeakGcRef<T>` references (currently
  weak refs skipped to avoid retention confusion)
