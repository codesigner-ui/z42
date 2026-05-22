# Design: Custom Region Allocator

## Architecture

```
                  ┌──────────────────────────────────┐
   alloc_object ─►│ Region<ScriptObject>             │
                  │  ┌──────────────────────────┐    │
                  │  │ chunks: Vec<Box<[E;N]>>  │    │   stable addrs;
                  │  │  chunk0: [E,E,E,E,...]   │    │   chunk boxes
                  │  │  chunk1: [E,E,E,E,...]   │    │   never move
                  │  │  ...                     │    │
                  │  └──────────────────────────┘    │
                  │  bump_idx: (chunk_idx, entry_idx)│
                  │  free_list: Vec<(u16, u16)>      │
                  └──────────────────────────────────┘
                                 │
                                 ▼
   GcRef<T> ──────────► (chunk_idx, entry_idx, generation)
                                 │
                                 ▼
   Region::resolve(handle) → &RegionEntry<T> ──► lock(Mutex<T>) ──► &T

   RegionEntry<T> {
     value:     T,                  // user data (owned)
     marked:    AtomicU8,           // mark bit (concurrent CAS)
     alive:     AtomicBool,         // tombstone flag
     generation: u32,               // bumped on tombstone (ABA prevention)
     finalizer: Option<FinalizerFn>,
   }
```

`ArcMagrGC` holds two regions:
- `region_object: Region<ScriptObject>`
- `region_array:  Region<Vec<Value>>`

These are the heap-resident `Value` variants. Other variants (`Closure`,
`Ref { kind: Field | Array }`) reuse one of the two regions via the inner
`GcRef<ScriptObject>` / `GcRef<Vec<Value>>`.

## Decisions

### Decision 1: Chunked storage (Vec<Box<[E; N]>>) for pointer stability

**问题**: 怎么保证 `RegionEntry` 地址在 region 增长时不变（`as_ptr` 必须稳定）？

**选项**:
- A — `Vec<RegionEntry<T>>` 直接存：增长 `Vec::push` 触发 realloc 时所有
  地址失效，破坏 `as_ptr` 契约
- B — `Vec<Box<RegionEntry<T>>>`：每 entry 单独 Box，每次 alloc 1 次
  malloc 单 entry — 失去 arena 的 alloc-batching 优势
- C — `Vec<Box<[RegionEntry<T>; CHUNK_SIZE]>>`：chunks 是 Box 单位，
  内部 entry 地址相对 chunk 起点稳定；chunk 数组本身扩容时只是 Vec
  增长（Box 指针在 Vec 内移动，但 Box 指向的 chunk 堆地址不变）

**决定**: C。`CHUNK_SIZE = 256`（典型 stdlib workload 一个 chunk 容纳
绝大多数对象；超出时 amortized alloc cost）。Entry 地址 = `chunks[ci].as_ptr().add(ei)`，
chunks Vec 的内部移动不影响 Box 指针。

**Allocator 复杂度**：
- 单次 alloc：O(1) bump pointer 或 O(1) free list pop
- 增长 chunk：每 256 entries 一次 malloc（chunk 大小 = 256 × sizeof(RegionEntry<T>)）

### Decision 2: 单 region per T-type (no size classes)

**问题**: `Region<ScriptObject>` + `Region<Vec<Value>>` 够了，还是需要按
对象大小分桶（small / medium / large）？

**决定**: 单 region per T-type for v1。理由：
- `ScriptObject` 是定长（`type_desc` + `slots: Vec<Value>` + `native: NativeData`）
- `Vec<Value>` 也是定长 header（`Vec` 是 24 字节）
- 实际可变内容（slots / elems）走 std `Vec` 的堆分配，不在 region 内
- 因此 region 中的 RegionEntry 是 statically sized — 适合 chunked storage 不需要分桶

未来如果 inline 内部 `Vec` 到 region（避免二次 malloc），再开 size-class spec。

### Decision 3: Finalizer fires at sweep only + `Std.GC.Finalize(x)` 显式 API

**问题**: 三个语义选择：
- A — sweep only（最纯 tracing）
- B — sweep + 显式 API
- C — sweep + 类似 RAII 的"strong ref count tracking"（保留 Arc 半语义）

**决定**: B。理由：
- A 太纯：用户失去 RAII 资源即时释放能力（fd / handle leak）
- C 复杂：tracking refs 等于 reintroduce Arc，A1 目的丧失
- B 平衡：默认 GC 释放 (correctness preserved)，需要立即释放有 explicit
  API。匹配 Java `AutoCloseable.close()` / .NET `IDisposable.Dispose()`

`Std.GC.Finalize(x)` 实现：
```rust
fn builtin_gc_finalize(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let target = &args[0];
    match target {
        Value::Object(gc) => {
            ctx.heap().finalize_now(gc);  // new trait method
        }
        Value::Array(gc) => {
            ctx.heap().finalize_now_array(gc);
        }
        _ => return Ok(Value::Null),  // no-op for primitives
    }
    Ok(Value::Null)
}
```

`finalize_now` 内部：
1. 找到 `RegionEntry`
2. 调用 `finalizer.take()` (one-shot, 同 mark-sweep 既有行为)
3. 调用 finalizer
4. Set `alive = false`, bump `generation`
5. Push slot to free list

### Decision 4: Tombstone via generation counter (ABA prevention)

**问题**: 老 `WeakGcRef::upgrade` 怎么处理 "entry 被回收且槽位被新对象重用" 的 ABA？

**选项**:
- A — `generation: u32` per entry，alloc 时不变，tombstone 时 ++。
  GcRef + WeakGcRef 都记 generation；upgrade 时检查匹配
- B — 不重用 slot，永远 grow（避免 ABA 但内存不释放）
- C — 用全局 64-bit handle（绝不重用）

**决定**: A。`u32` generation 给每个 slot 2^32 - 1 次重用前 wrap，对
GC workload 实际不会触发（每次 sweep 才 +1，要 sweep 几十亿次）。

实现：
```rust
struct GcRef<T> {
    chunk_idx: u16,
    entry_idx: u16,
    generation: u32,
    _phantom: PhantomData<T>,
}

struct WeakGcRef<T> {
    chunk_idx: u16,
    entry_idx: u16,
    generation: u32,
    _phantom: PhantomData<T>,
}

impl<T> WeakGcRef<T> {
    fn upgrade(&self, region: &Region<T>) -> Option<GcRef<T>> {
        let entry = region.resolve(self.chunk_idx, self.entry_idx);
        if entry.alive.load(Acquire) && entry.generation == self.generation {
            Some(GcRef { chunk_idx: self.chunk_idx, entry_idx: self.entry_idx,
                         generation: self.generation, _phantom: PhantomData })
        } else { None }
    }
}
```

GcRef 同样带 generation —— 这样 strong ref 也能检测"对象 since 被 Finalize 显式
tombstone 然后槽位重用"的情况。Strong ref 在那种情况下 `borrow` 应当报错
（contract violation：用户提前 Finalize 了还在用的对象）。Decision 6 详述。

### Decision 5: Strong GcRef 的访问也带 generation 检查

**问题**: 既然 `Std.GC.Finalize(x)` 能显式 tombstone 一个有 strong ref
的对象，那 strong ref 之后 `borrow` 该怎么办？

**选项**:
- A — `borrow` 检查 generation 匹配：不匹配 panic (`use-after-finalize`)
- B — 没检查：返回新对象的 borrow（极度危险，类型错误）
- C — 给 GcRef 加运行时 `is_valid` API，用户主动检查

**决定**: A。"显式 finalize 后还在用" 是用户 bug；release 模式 panic 是
正确的 fail-fast 行为。Debug 模式额外 `debug_assert` + 详细日志方便排查。

Performance: `borrow` 在 hot path，多一次比较 + 分支 ~1 ns。可接受
（对比每次 borrow 已经付 Mutex lock ~5-10 ns）。

### Decision 6: Mutex<T> stays on RegionEntry, not on region

**问题**: 并发访问 RegionEntry 的同步策略：
- A — Per-entry `Mutex<T>`（同当前 Arc<Mutex<T>>）
- B — Region-level RwLock，所有 entries 共享
- C — 无锁（仅 STW 时访问）

**决定**: A。同 multi-threading-foundation 已落地的并发模型（每对象独立锁），
避免引入新的同步语义。Per-entry Mutex 是 parking_lot 8 字节，整体仍比
今天的 Arc + Mutex + Mutex 紧凑。

### Decision 7: heap_registry 完全删除，sweep 直接走 regions

**问题**: 当前 sweep 走 `heap_registry: Vec<WeakRef>`，prune 时 `Weak::upgrade()`
检查死引用。新 region 模型下还需要 registry 吗？

**决定**: 不需要。Region 本身就是 authoritative liveness store。Sweep 直接
walk `region_object.chunks` + `region_array.chunks`：
- `alive == true && marked == 1` → reset marked, retain
- `alive == true && marked == 0` → finalizer fire, alive=false, gen++, push to free_list
- `alive == false` → 跳过 (already tombstoned)

性能：从"O(N) atomic upgrades on Weak"降到"O(N) load 1 byte"。

### Decision 8: GcRef::clone 不再带 atomic op

**问题**: `GcRef::clone` 当前是 `Arc::clone`（1 atomic fetch_add）。新模型下？

**决定**: integer copy。Handle 是 `(u16, u16, u32)` 12 字节，clone = memcpy
12 字节，0 atomic op。

这是 A1 最大的性能收益：每 `GcRef::clone()` 省 ~2-4 ns × 多倍 hot path
调用。Closure capture、参数传递、Object 字段读取 都走 GcRef::clone。

### Decision 9: Move semantics for GcRef (vs Copy)

**问题**: GcRef 现在是 `(u16, u16, u32)` 12 字节，能不能 `#[derive(Copy)]`？

**选项**:
- A — `Copy`：用户写 `let b = a;` 之后 `a` 仍可用
- B — `Clone` 不 `Copy`：用户需要 `let b = a.clone()`，强制显式

**决定**: B (Clone-only)。理由：
- 当前 GcRef 是 `Clone` not `Copy`（Arc<...> 限制），让 API 行为保持一致
- `Copy` 会让用户写出"持久持有引用但意识不到"的代码 — 对 GC root 跟踪
  不利
- `Clone` 显式让代码读起来 reference 的 alive scope 更清楚

### Decision 10: Old data variants (Closure, Ref) reuse inner GcRef

**问题**: `Value::Closure { env: GcRef<Vec<Value>> }` 和 `Value::Ref { ... }`
里面是 `GcRef<...>`。这些不变？

**决定**: 不变。`Value::Closure { env: GcRef<Vec<Value>> }` 里的 GcRef
变成新 handle 形态后，整体 Value 的尺寸缩小一点（Arc 8B → Handle 12B —
增大一点其实）。但 alloc 路径变 region，得到性能收益。

实际 Value 尺寸：当前 32 bytes（含 enum discriminant + Arc）；新模型 28-32 bytes
（handle）。无重大变化。

## Implementation Notes

### Region<T> 数据结构

```rust
const CHUNK_SIZE: usize = 256;

pub struct Region<T> {
    chunks: Vec<Box<[MaybeUninit<RegionEntry<T>>; CHUNK_SIZE]>>,
    next_bump: (u16, u16),  // (chunk_idx, entry_idx_within_chunk)
    free_list: Vec<(u16, u16)>,
}

pub struct RegionEntry<T> {
    value:      parking_lot::Mutex<T>,
    marked:     AtomicU8,
    alive:      AtomicBool,
    generation: AtomicU32,
    finalizer:  parking_lot::Mutex<Option<FinalizerFn>>,
}
```

### Alloc fast path

```rust
impl<T> Region<T> {
    pub fn alloc(&mut self, value: T) -> Handle {
        // 1. Free list 优先（重用槽）
        if let Some((ci, ei)) = self.free_list.pop() {
            let entry = unsafe { self.chunks[ci as usize][ei as usize].assume_init_mut() };
            *entry.value.lock() = value;
            entry.alive.store(true, Release);
            // generation 已由 sweep tombstone 时 bump
            return Handle { ci, ei, gen: entry.generation.load(Acquire) };
        }

        // 2. Bump pointer
        let (ci, ei) = self.next_bump;
        let chunk = if (ci as usize) >= self.chunks.len() {
            // grow
            self.chunks.push(Box::new(std::array::from_fn(|_| MaybeUninit::uninit())));
            // ... initial chunk
            &mut self.chunks[ci as usize]
        } else {
            &mut self.chunks[ci as usize]
        };

        let entry = RegionEntry::new(value);
        chunk[ei as usize] = MaybeUninit::new(entry);
        // 推进 next_bump...
        ...
    }
}
```

### Sweep walks regions

```rust
fn sweep_phase(&self) -> u64 {
    let mut freed = 0;
    for region in [&self.region_object, &self.region_array] {
        for (ci, chunk) in region.chunks.iter().enumerate() {
            for ei in 0..CHUNK_SIZE {
                let entry = unsafe { chunk[ei].assume_init_ref() };
                if !entry.alive.load(Acquire) { continue; }

                let was_marked = entry.marked.swap(0, AcqRel) != 0;
                if !was_marked {
                    // Finalize + tombstone
                    if let Some(f) = entry.finalizer.lock().take() {
                        f();
                    }
                    entry.alive.store(false, Release);
                    entry.generation.fetch_add(1, AcqRel);
                    region.free_list.lock().push((ci as u16, ei as u16));
                    freed += size_of::<RegionEntry<T>>() as u64;
                }
            }
        }
    }
    freed
}
```

### `Std.GC.Finalize(x)` builtin

```rust
fn builtin_gc_finalize(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match args.get(0).cloned().unwrap_or(Value::Null) {
        Value::Object(gc) => ctx.heap().finalize_now_object(&gc),
        Value::Array(gc)  => ctx.heap().finalize_now_array(&gc),
        _ => {}
    }
    Ok(Value::Null)
}
```

## Testing Strategy

### Unit tests (P3-P5)
- `region_alloc_creates_first_chunk_on_demand`
- `region_alloc_grows_chunks_when_full`
- `region_alloc_pointer_stability_across_grow` (critical — `as_ptr` test)
- `region_alloc_free_list_reuses_tombstoned_slot`
- `region_alloc_bumps_generation_on_tombstone`
- `gc_ref_clone_is_integer_copy_no_atomic` (verify via PMC or
  cargo asm review)
- `weak_gc_ref_upgrade_after_tombstone_returns_none`
- `weak_gc_ref_upgrade_after_slot_reused_returns_none` (ABA test)
- `finalizer_does_not_fire_on_scope_exit`
- `finalizer_fires_at_sweep_when_unreachable`
- `std_gc_finalize_fires_finalizer_immediately`
- `use_after_finalize_panics_in_release` (Decision 5)
- `concurrent_mark_with_region_backing_preserves_chain`
- `concurrent_mark_with_region_backing_frees_cycle`

### Integration (P5)
- `cross_thread_smoke` re-runs entirely (Arc → Region transparent
  to user-level z42 code; should pass unchanged modulo finalizer
  timing tests)
- `Z42_GC_MODE=concurrent ./scripts/test-stdlib.sh` → 72/72 GREEN

### Bench (P5)
- Existing `gc_cycle_bench`: re-run, compare with pre-spec baseline
  via git worktree at HEAD~before-spec
- New `alloc_throughput_bench`: tight loop measuring obj/sec alloc
  — expect 5-10x improvement on alloc path
- `sweep_overhead_bench`: STW sweep on 10k objects → measure
  per-object overhead

## Phasing

Implementation in 4 phases, each independently committable + GREEN.

### P0: Region<T> + RegionEntry<T> infrastructure
- Add `gc/region.rs` with `Region<T>`, `RegionEntry<T>`, chunk
  allocation, free list, alloc / resolve / tombstone API
- Standalone unit tests in `gc/region_tests.rs` (alloc, grow,
  pointer stability, free list reuse, generation bumping)
- Cargo build green; no callers yet
- Commit

### P1: GcRef<T> / WeakGcRef<T> rewrite, ArcMagrGC alloc routes
- Rewrite `gc/refs.rs`: GcRef<T> + WeakGcRef<T> become handle types
  with generation counter
- ArcMagrGC gains `region_object` + `region_array` fields
- `alloc_object` / `alloc_array` route through regions
- `heap_registry: Vec<WeakRef>` removed; iterate_live_objects + sweep
  walk regions directly
- Migrate existing `gc/refs.rs` tests + `arc_heap_tests` to new
  semantics; bulk of effort is here
- Cargo + GC tests + runtime gate GREEN
- Commit

### P2: Finalizer semantics migration
- Move finalizer dispatch from `GcAllocation::Drop` (gone) to
  `sweep_phase`
- Migrate ~10 `arc_heap_tests/finalization.rs` tests: "Drop fires
  finalizer" → "force_collect fires finalizer" or "Std.GC.Finalize
  fires immediately"
- Add `Std.GC.Finalize(x)` builtin + Std/GC.z42 wrapper
- Audit corelib for Drop-as-cleanup patterns; migrate
  `builtin_process_handle_drop` → explicit `Std.IO.Process.Close()`
  API or accept "close at sweep" with regression test update
- `test-all.sh --scope=full` GREEN
- Commit

### P3: Bench + docs + archive
- Add `alloc_throughput_bench` + `sweep_overhead_bench` to
  `gc_cycle_bench.rs`
- Compare pre-spec (worktree at pre-P0 commit) vs post-spec via real
  bench numbers
- Update `vm-architecture.md` "GC heap backing" chapter, A1 entry
  "future → landed", Phase table, finalizer contract documentation
- Archive `docs/spec/changes/add-custom-allocator/` →
  `docs/spec/archive/YYYY-MM-DD-add-custom-allocator/`
- Commit

## Deferred / Future Work

### add-generational-gc (A3)
- Promotes survived objects from young region to old region;
  card-marking on barrier writes; uses the region infrastructure
  from this spec

### add-size-class-regions
- If profiling shows fragmentation issues with single region per
  T-type, introduce small/medium/large size classes per region

### add-per-thread-arena
- Per-VmContext local bump-pointer arenas for alloc fast path
  without lock contention; periodic batch-flush to global region

### add-handle-compression
- If GcRef size becomes a bottleneck, compress (chunk_idx,
  entry_idx, gen) into a single u64

### add-mmtk-binding
- Replace Region<T> with MMTk's allocator via VMBinding trait;
  Region API already shaped to fit MMTk's `Mutator::alloc`
