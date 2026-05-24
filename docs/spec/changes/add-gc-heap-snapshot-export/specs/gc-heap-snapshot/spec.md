# Spec: GC Heap Snapshot Export

## ADDED Requirements

### Requirement: `GraphSnapshot` type

#### Scenario: Default snapshot is just the synthetic root
- **WHEN** `build_graph_snapshot` is called on a freshly-created heap
  with no script allocations
- **THEN** the resulting `GraphSnapshot` has exactly 1 node
  (synthetic root, name = `"(GC roots)"`)
- **AND** has 0 edges
- **AND** has a strings table containing at least the root-name string

#### Scenario: Each live heap object becomes a node
- **WHEN** `build_graph_snapshot` is called on a heap with N live
  `Value::Object` / `Value::Array` instances
- **THEN** the snapshot has `1 + N` nodes (synthetic root + N)
- **AND** every alive value's pointer appears in the node id map
  exactly once (deduped by pointer identity)

#### Scenario: Object slot references emit `property` edges
- **WHEN** a `ScriptObject` slot holds another `Value::Object` /
  `Value::Array`
- **THEN** the snapshot emits an `EdgeType::Property` edge from the
  owner to the child
- **AND** the edge's `name_or_index` is the string-table index of the
  TypeDesc field name at that slot (fallback `"slot{i}"` if unnamed)

#### Scenario: Array element references emit `element` edges
- **WHEN** a `Vec<Value>` index holds another `Value::Object` /
  `Value::Array`
- **THEN** the snapshot emits an `EdgeType::Element` edge
- **AND** the edge's `name_or_index` is the **integer index** `i`
  (not a string-table reference)

#### Scenario: Roots emit `shortcut` edges from the synthetic root
- **WHEN** the heap exposes pinned roots / external roots via
  `for_each_root`
- **THEN** each rooted object receives one `EdgeType::Shortcut` edge
  from node id 0 (the synthetic root)

#### Scenario: Cycles do not loop the builder
- **WHEN** the heap contains `a.next = b; b.next = a`
- **THEN** `build_graph_snapshot` terminates in finite time
- **AND** emits exactly one node per object (a, b) plus root
- **AND** emits exactly two property edges (a→b and b→a)

### Requirement: V8 `.heapsnapshot` JSON layout

#### Scenario: Top-level keys present
- **WHEN** `serialize_v8_heapsnapshot(&snapshot)` is called
- **THEN** the JSON object contains keys: `snapshot`, `nodes`,
  `edges`, `trace_function_infos`, `trace_tree`, `samples`,
  `locations`, `strings`

#### Scenario: `snapshot.meta` declares the field layout
- **WHEN** JSON `snapshot.meta` is read
- **THEN** `node_fields` is exactly
  `["type","name","id","self_size","edge_count","trace_node_id","detachedness"]`
- **AND** `edge_fields` is exactly `["type","name_or_index","to_node"]`
- **AND** `node_types[0]` is the 14-element type enum starting with
  `"hidden"`
- **AND** `edge_types[0]` is the 7-element type enum starting with
  `"context"`

#### Scenario: `nodes` array length equals `node_count * 7`
- **WHEN** `snapshot.node_count` declares N nodes
- **THEN** the flat `nodes` array has exactly `7 * N` integer entries
  (per the 7 `node_fields`)

#### Scenario: `edges` array length equals `edge_count * 3`
- **WHEN** `snapshot.edge_count` declares M edges
- **THEN** the flat `edges` array has exactly `3 * M` integer entries

#### Scenario: Empty arrays for unsupported sections
- **WHEN** v1 snapshot is serialized
- **THEN** `trace_function_infos`, `trace_tree`, `samples`, and
  `locations` are all `[]`
- **AND** `snapshot.trace_function_count` is `0`

#### Scenario: Strings table contains every referenced name
- **WHEN** any node has a non-zero `name_idx` or any edge has a
  string-typed `name_or_index`
- **THEN** the index dereferences into the `strings` array
- **AND** the array contains no duplicate strings (dedup'd)

### Requirement: `Std.GC.WriteHeapSnapshot` z42 builtin

#### Scenario: Writes the snapshot to the given path
- **WHEN** z42 code calls `Std.GC.WriteHeapSnapshot("/tmp/x.heapsnapshot")`
- **THEN** a file at `/tmp/x.heapsnapshot` is created
- **AND** the file's contents parse as valid JSON
- **AND** the JSON has the V8 layout described above

#### Scenario: Returns bytes written
- **WHEN** the builtin completes successfully
- **THEN** the return value is the number of bytes written (as `long`)
- **AND** the return value is > 100 (synthetic root + meta alone
  exceed a hundred bytes)

#### Scenario: Write failure throws
- **WHEN** the path is invalid / not writable
- **THEN** the builtin throws (z42-side `Std.IOException` or generic
  via `anyhow` → vm exception)
- **AND** no partial file is left at the path (caller's responsibility
  to ensure parent dir exists)

## MODIFIED Requirements

无 — 这是纯增量 spec，不动既有 trait 或 type 形状。`HeapSnapshot`
（aggregate per-type）类型保留不动；`GraphSnapshot` 是独立新类型。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 不变
- [x] VM interp — 不变（builtin 走 dispatch table，标准入口）
- [x] JIT — 不变
- [x] GC subsystem — `gc/snapshot.rs` + 用既有 `iterate_live_objects` /
  `scan_object_refs` / `for_each_root` 三个 trait 方法走 graph build
- [x] corelib — 1 个新 builtin（append 末尾，preserve BuiltinIds）
- [x] stdlib — `Std.GC.z42` 加 1 个 extern 声明

## IR Mapping

无新 IR 指令。
