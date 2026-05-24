# Design: GC Heap Snapshot — Streaming Serializer

## Architecture

```
 corelib/gc.rs::builtin_gc_write_heap_snapshot
   │
   ├─ gc::snapshot::build_graph_snapshot(heap) → GraphSnapshot
   │    (unchanged — still O(N+E) memory)
   │
   ├─ File::create(path)
   │    └─ BufWriter::new(file)
   │
   └─ gc::snapshot::serialize_v8_heapsnapshot_to(snap, &mut writer)
        │                            ↑
        │                            └ implements io::Write
        ├─ writes JSON tokens directly to writer
        ├─ no intermediate String allocation
        └─ returns u64 bytes-written

   Caller drops writer → BufWriter flushes → File closes.
   `builtin_gc_write_heap_snapshot` returns bytes_written as i64.
```

## Decisions

### Decision 1: Generic `<W: Write>`, not `&mut dyn Write`

**问题**：用泛型 `<W: Write>` 还是 trait object `&mut dyn Write`？

**选项**：
- A — Generic `<W: Write>`：monomorphisation；inlines well；
  zero per-call vtable cost
- B — `&mut dyn Write`：单 codegen instance；call sites cheaper
  to compile；但每次 `write_all` 是 vtable indirect

**决定**：A. snapshot 路径不是 hot loop（一次 build + serialize
per `WriteHeapSnapshot` call），但 binary size 增加很小（一个
monomorphised instance for `BufWriter<File>` 即所有现实 caller），
代码更简洁。

### Decision 2: Return `io::Result<u64>` byte count

**问题**：streaming serializer 怎么报告写入字节数？

**选项**：
- A — 返 `io::Result<u64>`：bytes-written counter；caller 无需
  额外 stat 文件
- B — 返 `io::Result<()>`：让 caller 自己 stat / wrap counting
  Writer

**决定**：A. counter 顺手维护（每个 `write_all` 调用前 += len）；
caller 直接拿到精确字节数，回到 z42 script 当 long 返。Counting
wrapper 是 over-engineering for v1。

### Decision 3: `escape_json_str` takes `&mut W`, not `&mut String`

**问题**：现有 `escape_json_str(s, &mut String)` 怎么 retrofit？

**选项**：
- A — 改签名为 `fn(&str, &mut W) -> io::Result<()>` where `W: Write`
- B — 保留原 String 版本 + 加 stream 版本（重复逻辑）
- C — 用 `write!(writer, "{:?}", s)` 但 Rust Debug 不严格 JSON-safe

**决定**：A. 单一实现避免漂移。in-memory wrapper 仍能用同一 helper
（`Vec<u8>` 实现 `Write`）。

### Decision 4: In-memory wrapper preserved (back-compat)

**问题**：现有的 `serialize_v8_heapsnapshot -> String` 删掉还是
保留？

**选项**：
- A — 删：所有 caller 改 streaming
- B — 保留作为薄包装，驱动 `Vec<u8>` writer → `String::from_utf8`

**决定**：B. snapshot_tests.rs 的几个测试已经按 `&str` JSON 调用
`json.contains(...)` 验证 layout — 改写它们多余的工作；in-memory
版本永远是 streaming 的薄包装，无逻辑漂移风险。

```rust
pub fn serialize_v8_heapsnapshot(snap: &GraphSnapshot) -> String {
    let mut buf: Vec<u8> = Vec::with_capacity(1024 + snap.nodes.len() * 48);
    serialize_v8_heapsnapshot_to(snap, &mut buf)
        .expect("Vec<u8>::write never fails");
    // SAFETY: We only emit ASCII-safe JSON; strings are properly
    // escaped via `escape_json_str`. UTF-8 invariant holds.
    unsafe { String::from_utf8_unchecked(buf) }
}
```

### Decision 5: BufWriter capacity

**问题**：caller 用 `BufWriter::new(File)` 默认 8 KiB buffer 够吗？

**选项**：
- A — 默认 8 KiB
- B — 64 KiB 大 buffer
- C — `File` 直接（无 BufWriter）

**决定**：A. 8 KiB 默认对 SSD 写入足够；每 write_all 调用都是
typical < 256 byte 一段 JSON token + key — buffer aggregation
良好。极端 case (slow disk) 用户传自己的 BufWriter 即可（API
是 generic）。

### Decision 6: Byte-identical output guaranteed

streaming 路径与现有 in-memory 路径**必须**产生 byte-identical
JSON。两条路径共享同一组 `write!(writer, ...)` 调用 + 同一个
`escape_json_str` helper。一个回归测试在 snapshot_tests.rs 验：

```rust
#[test]
fn streaming_and_in_memory_produce_identical_bytes() {
    let snap = build_a_known_snapshot();
    let from_string = serialize_v8_heapsnapshot(&snap);
    let mut from_stream: Vec<u8> = Vec::new();
    let n = serialize_v8_heapsnapshot_to(&snap, &mut from_stream).unwrap();
    assert_eq!(n as usize, from_string.len());
    assert_eq!(from_stream, from_string.as_bytes());
}
```

## Implementation Notes

### Streaming serializer skeleton

```rust
// gc/snapshot.rs

use std::io::{self, Write};

pub fn serialize_v8_heapsnapshot_to<W: Write>(
    snap: &GraphSnapshot,
    writer: &mut W,
) -> io::Result<u64> {
    let mut n_bytes: u64 = 0;
    let node_count = snap.nodes.len();
    let edge_count = snap.edges.len();

    // Build idx_map (same as in-memory version).
    let mut idx_map: HashMap<u32, u32> = HashMap::with_capacity(node_count);
    for (i, n) in snap.nodes.iter().enumerate() {
        idx_map.insert(n.id, i as u32);
    }

    let mut edges_by_src: Vec<Vec<&EdgeRec>> = vec![Vec::new(); node_count];
    for e in &snap.edges {
        if let Some(b) = edges_by_src.get_mut(e.from_node_idx as usize) {
            b.push(e);
        }
    }

    macro_rules! w {
        ($($arg:tt)*) => {{
            let s = format!($($arg)*);
            writer.write_all(s.as_bytes())?;
            n_bytes += s.len() as u64;
        }};
    }
    macro_rules! wb {
        ($lit:expr) => {{
            writer.write_all($lit)?;
            n_bytes += $lit.len() as u64;
        }};
    }

    wb!(b"{\"snapshot\":{");
    wb!(META_HEADER.as_bytes());
    w!(",\"node_count\":{}", node_count);
    w!(",\"edge_count\":{}", edge_count);
    wb!(b",\"trace_function_count\":0}");

    wb!(b",\"nodes\":[");
    for (i, n) in snap.nodes.iter().enumerate() {
        if i > 0 { wb!(b","); }
        w!("{},{},{},{},{},{},0",
            n.node_type as u8, n.name_idx, n.id,
            n.self_size, n.edge_count, n.trace_node_id);
    }
    wb!(b"]");

    wb!(b",\"edges\":[");
    let mut first_edge = true;
    for src_edges in &edges_by_src {
        for e in src_edges {
            if !first_edge { wb!(b","); }
            first_edge = false;
            w!("{},{},{}",
                e.edge_type as u8,
                e.name_or_index,
                to_node_offset(e.to_node_id, &idx_map));
        }
    }
    wb!(b"]");

    wb!(b",\"trace_function_infos\":[]");
    wb!(b",\"trace_tree\":[]");
    wb!(b",\"samples\":[]");
    wb!(b",\"locations\":[]");

    wb!(b",\"strings\":[");
    for (i, s) in snap.strings.iter().enumerate() {
        if i > 0 { wb!(b","); }
        n_bytes += escape_json_str_to(s, writer)?;
    }
    wb!(b"]");

    wb!(b"}");
    Ok(n_bytes)
}

fn escape_json_str_to<W: Write>(s: &str, writer: &mut W) -> io::Result<u64> {
    let mut n: u64 = 0;
    writer.write_all(b"\"")?; n += 1;
    let mut buf = [0u8; 4];
    for c in s.chars() {
        let bytes: &[u8] = match c {
            '"'  => b"\\\"",
            '\\' => b"\\\\",
            '\n' => b"\\n",
            '\r' => b"\\r",
            '\t' => b"\\t",
            '\x08' => b"\\b",
            '\x0c' => b"\\f",
            c if (c as u32) < 0x20 => {
                let s = format!("\\u{:04x}", c as u32);
                writer.write_all(s.as_bytes())?;
                n += s.len() as u64;
                continue;
            }
            c => {
                let enc = c.encode_utf8(&mut buf);
                writer.write_all(enc.as_bytes())?;
                n += enc.len() as u64;
                continue;
            }
        };
        writer.write_all(bytes)?;
        n += bytes.len() as u64;
    }
    writer.write_all(b"\"")?; n += 1;
    Ok(n)
}
```

### builtin update

```rust
// corelib/gc.rs

pub fn builtin_gc_write_heap_snapshot(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let path = match args.first() {
        Some(Value::Str(s)) => s.clone(),
        _ => return Err(anyhow!("__gc_write_heap_snapshot: expected string path")),
    };
    let snap = crate::gc::snapshot::build_graph_snapshot(ctx.heap());
    let file = std::fs::File::create(path.as_str())
        .map_err(|e| anyhow!("__gc_write_heap_snapshot: create {}: {}", path, e))?;
    let mut writer = std::io::BufWriter::new(file);
    let n_bytes = crate::gc::snapshot::serialize_v8_heapsnapshot_to(&snap, &mut writer)
        .map_err(|e| anyhow!("__gc_write_heap_snapshot: write {}: {}", path, e))?;
    writer.flush()
        .map_err(|e| anyhow!("__gc_write_heap_snapshot: flush {}: {}", path, e))?;
    Ok(Value::I64(n_bytes as i64))
}
```

`BufWriter::flush()` is explicit (Drop-flush only when buffer is
already empty); we want errors surfaced before returning success.

## Testing Strategy

### Unit tests (P0)

In `gc/snapshot_tests.rs`:

- **Existing tests untouched**: all `serialized_json_has_expected_structure`
  / `flat_arrays_have_correct_lengths` / `string_table_is_deduped` still
  pass — they go through `serialize_v8_heapsnapshot(&snap)` which is
  now the streaming wrapper.

- New tests:
  - `streaming_and_in_memory_produce_identical_bytes` — fixed
    snapshot → streaming bytes == in-memory bytes (full byte match)
  - `streaming_byte_count_matches_string_length` — streaming returns
    `n_bytes == in_memory.len() as u64`
  - `streaming_writes_to_file_buffered_writer_round_trips` — open
    tempfile + `BufWriter::new` + `serialize_v8_heapsnapshot_to` +
    flush + read back → bytes equal in-memory version

### Integration test (P0)

End-to-end stdlib test `gc_heap_snapshot_streaming.z42` — separate
from the existing B3 e2e (so the new path is independently asserted):

```z42
[Test]
void test_write_heap_snapshot_streaming_writes_file() {
    string path = "/tmp/z42-test-stream.heapsnapshot";
    long bytes = GC.WriteHeapSnapshot(path);
    Assert.True(bytes > 100);
    string content = File.ReadAllText(path);
    Assert.Equal(bytes, content.Length);
    File.Delete(path);
}
```

(Essentially redundant with the existing B3 e2e but documents the
streaming path as an independent contract.)

## Phasing

- **P0**: `gc/snapshot.rs` streaming serializer + escape_json_str_to
  + in-memory wrapper retained + 3 unit tests + builtin update.
  ~150 LOC.
- **P1**: gc.md "Heap snapshot export" subsection 加 streaming
  注解 + Phase 表加行 + B3 Deferred sub-list 标 landed +
  archive.

2 commits, ~0.5-1 session total.

## Deferred / Future Work

### add-gc-snapshot-walker-streaming

- Interleave walker with serialization — emit each node + its edges
  immediately, no GraphSnapshot intermediate. Halves memory again.
  Complexity: V8 wants `node_count` / `edge_count` pre-declared in
  meta; need either two-pass or estimate-and-patch (sparse seek).
- Needed only for >1 GB heaps.

### add-gc-snapshot-gzip

- Transparent gzip wrap. `flate2` already on the crate dep tree
  (via z42.compression). 5-10× file-size reduction for typical
  snapshots.

### add-gc-snapshot-progress-callback

- Periodic callback to host during long writes ("X bytes written
  so far") for UI / cancellation.
