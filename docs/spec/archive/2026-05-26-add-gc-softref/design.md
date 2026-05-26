# Design: add-gc-softref

## Architecture

```
z42 script                   Rust corelib              GC internals
─────────────────────────────────────────────────────────────────────
SoftHandle.Create(obj)
  → __soft_handle_create ──► heap.register_soft_ref(val)
                               → SoftGcRef::new(entry)
                               → soft_refs.push(erased entry ptr)
                               → entry.soft_ref_count++
                               → returns Value::Object(SoftHandle obj)

SoftHandle.Get()
  → __soft_handle_get ──────► heap.soft_ref_get(handle)
                               → soft_ref.upgrade()
                               → Some(val) | None (→ null)

GC.ForceCollect()
  → run_cycle_collection_stw:
    1. mark_phase()           (soft refs NOT followed as roots)
    2. revive_soft_refs()  ◄──── NEW: if pressure < threshold,
       for each soft entry:        re-mark unmarked soft-ref targets
         if !is_marked() && upgrade().is_some():
           if used_ratio < SOFT_THRESHOLD: entry.mark()
           // else: leave unmarked → swept
    3. sweep_phase()          (unchanged: tombstones unmarked entries)
    4. soft_refs.retain(|e| e.is_alive())  ← prune dead entries
```

## Decisions

### Decision 1: Where to store the registry

**问题：** revive pass 需要迭代所有当前活跃的软引用 entry。

**选项：**
- A — `ArcMagrGcInner.soft_refs: Mutex<Vec<ErasedSoftEntry>>` （type-erased raw ptr + generation）
- B — `RegionEntry<T>` 加 `soft_ref_count: AtomicU32`，revive pass 迭代全堆扫描 has_soft

**决定：** 选 A。Registry 仅含软引用数量（通常极少），revive pass 是 O(soft_refs) 而非 O(heap)。B 的全堆扫描代价与堆大小线性相关，与软引用数量无关，过于昂贵。

**ErasedSoftEntry 结构：**
```rust
pub(crate) struct ErasedSoftEntry {
    ptr: NonNull<u8>,          // 指向 RegionEntry<T> 的类型擦除指针
    generation: u32,           // 用于 liveness 检查（同 WeakGcRef）
    kind: ErasedKind,          // Object | Array（决定如何重新构造 Value）
}
enum ErasedKind { Object, Array }
```

RevivePass 实现：
```rust
fn revive_soft_refs(&self) {
    let used_ratio = used_bytes as f64 / max_bytes as f64; // 0.0 if max==0
    let threshold = env_soft_threshold(); // default 0.80
    if max_bytes == 0 || used_ratio < threshold {
        // below pressure: revive all soft-ref targets that are still alive
        for entry in soft_refs.iter() {
            if !entry.is_alive() { continue; }
            if !entry.is_marked() { entry.mark(); }
        }
    }
    // above pressure: leave unmarked soft-ref targets to be swept
}
```

### Decision 2: SoftGcRef vs reusing WeakGcRef

**问题：** SoftGcRef 和 WeakGcRef 在结构上完全相同（`NonNull<RegionEntry<T>> + u32`）。是否复用？

**决定：** 新建 `SoftGcRef<T>` 类型别名或 newtype（不复用 WeakGcRef），原因：
- Drop 行为不同（SoftGcRef::drop 需要从注册表删除 + 递减 soft_ref_count）
- 类型区分防止意外用 WeakGcRef 跳过 soft 语义

**实现：** `SoftGcRef<T>` 内部持有 `WeakGcRef<T>` + `ptr_key: usize`（用于从注册表 O(1) 删除）

### Decision 3: script API — SoftHandle vs SoftRef<T>

**问题：** 泛型 `SoftRef<T>` 更类型安全，但 z42 尚未有泛型。

**决定：** `Std.SoftHandle`（非泛型），与现有 `Std.WeakHandle` API 形状一致。L2 泛型就位后再升级，延后见 Deferred 段。

### Decision 4: 压力阈值来源

**问题：** 阈值硬编码 or 环境变量 or 脚本 API？

**决定：** 环境变量 `Z42_GC_SOFT_THRESHOLD`（f64，`[0.0, 1.0]`，parse 失败 default 0.80）。理由：
- 允许嵌入用户在不修改 z42 代码的情况下调整
- 不需要新的脚本 API（现有 `GC.*` 已经很大）
- 未来可升级为 `GC.SetSoftThreshold(f64)` builtin，延后

## Implementation Notes

### SoftHandle.z42 sketch

```z42
namespace Std;

public class SoftHandle {
    private object _native; // Rust-side backing (opaque)

    [Native("__soft_handle_create")]
    public static SoftHandle Create(object target) { }

    [Native("__soft_handle_get")]
    public object Get() { }
}
```

### Builtin signatures

```rust
// __soft_handle_create(target: Value) -> Value (SoftHandle object)
fn builtin_soft_handle_create(ctx: &VmContext, args: &[Value]) -> anyhow::Result<Value>

// __soft_handle_get(self: Value) -> Value (target or Null)
fn builtin_soft_handle_get(ctx: &VmContext, args: &[Value]) -> anyhow::Result<Value>
```

### Revive pass insertion point

In `arc_heap.rs` `run_cycle_collection_stw`:
```
mark_phase() → revive_soft_refs() → sweep_phase() → notify_finalizers()
```

The revive pass runs between mark and sweep, re-marking entries before the sweeper can tombstone them.

### Edge cases

- `max_bytes == 0`（无限堆）：`used_ratio = 0.0 < threshold` → 软引用永不被 GC 回收
- `threshold >= 1.0`：永不回收（等价无限堆）
- `threshold <= 0.0`：每次 GC 都回收（等价 WeakRef）
- 并发安全：registry 在 `Mutex<ArcMagrGcInner>` 持有时访问；`revive_soft_refs` 在 STW 内调用，无并发问题

## Testing Strategy

- Golden test 1 `gc_softhandle_basic`: unlimited heap → ForceCollect → Get() 非 null
- Golden test 2 `gc_softhandle_pressure`: SetMaxHeapBytes(small) + 填充对象 + ForceCollect → Get() null
- Golden test 3 `gc_softhandle_strong_wins`: 强引用同时存在 → 即使压力下 Get() 仍非 null
