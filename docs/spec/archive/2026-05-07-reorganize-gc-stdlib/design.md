# Design: Reorganize GC stdlib + Add Unified GCHandle

## Architecture

```
z42 stdlib (script)                Rust corelib                          GC heap
─────────────────                  ───────────────                       ─────────
struct GCHandle ──[Native]──→ corelib/gc.rs ─[trait]─→ MagrGC::HandleTable
  _slot: long                    builtin_gc_handle_*                  HandleSlab:
                                                                        Vec<Option<HandleEntry>>
                                                                      free_list:
                                                                        Vec<u64>
                                                                      HandleEntry:
                                                                        Strong(Rc<RefCell<Object>>)
                                                                        Weak(Weak<RefCell<Object>>)

class HeapStats ──[Native]──→ builtin_gc_stats ─[trait]─→ MagrGC::stats() → HeapStats
                              (project Rust struct → z42 instance with 7 long fields)
```

## Decisions

### Decision 1: struct vs class for GCHandle

**问题**：z42 端 GCHandle 用 struct 还是 class？

**选项**：
- A — class：reference type；多个变量持同一 GCHandle 实例自然共享 backing
- B — struct：value type；拷贝时字段复制，独立实例
- C — struct + corelib backing：struct 含 slot ID，实际 backing 在 corelib
  HandleTable；拷贝共享 slot

**决定**：选 C。

**理由**：与 C# `System.Runtime.InteropServices.GCHandle` 完全对齐
（C# 是 IntPtr 指向 CLR handle table；z42 是 long slot ID 指向 Rust
HandleTable）。Free 后所有 alias 同步失效，符合 C# 用户习惯。纯 z42 stdlib
struct 拷贝独立分裂的"假 struct"语义会让 Free 失去意义。

### Decision 2: HandleTable storage strategy

**问题**：HandleTable 怎么实现？

**选项**：
- A — `HashMap<u64, HandleEntry>` + atomic counter：插入 O(1) 平均，但堆碎片
- B — `Vec<Option<HandleEntry>>` slab + `Vec<u64>` free list：O(1) alloc/free，
  slot 复用紧凑

**决定**：选 B。

**理由**：slab 简单、cache-friendly，free list 让 slot 立即复用避免单调增长。
`_slot=0` reserved 作"未分配" sentinel（vec[0] 永不使用）。

### Decision 3: HandleEntry kind discriminator

**问题**：Strong / Weak 在 entry 内部怎么表达？

**决定**：Rust enum

```rust
pub enum HandleEntry {
    Strong(Rc<RefCell<Object>>),
    Weak(Weak<RefCell<Object>>),
}
```

Phase 3 加 Pinned variant 时是兼容性扩展（既有 match 加分支即可，z42 端
GCHandleType 加值即可）。

### Decision 4: Strong slot 在 RC 模式下的语义

**问题**：Phase 1 RC 模式下 "Strong handle" 实际怎么 anchor 目标？

**决定**：`Rc::clone(target)` 存进 slot。

**理由**：z42 RC 模式下，`Rc::clone` = 强引用 +1。slot 持 clone 直到 Free()
时 drop，Rc count 才回落。这天然实现 "anchor 目标直到显式释放"。无需新
GC root tracking 机制。

### Decision 5: Atomic value AllocWeak 行为

**问题**：`GCHandle.AllocWeak(42)` 怎么处理？z42 atomic values（I64/Str/etc.）
不是 Rc-allocated，无法 weak-ref。

**选项**：
- A — 抛 ArgumentException
- B — 返回 `_slot=0` 的 GCHandle（IsAllocated=false）
- C — 内部退化为 Strong 模式（原值 clone）

**决定**：选 B。

**理由**：与既有 `WeakHandle.MakeWeak(atomic) → null` 行为对齐；用户用
`if (h.IsAllocated)` 判断而非 try/catch 更顺手。Strong 退化（C）会让 Kind
属性与构造时输入不一致，更隐蔽。

### Decision 6: HeapStats.MaxBytes sentinel

**问题**：Rust `HeapStats.max_bytes: Option<u64>`，z42 无 `Optional<T>`，
怎么投影？

**决定**：`-1` 表示 unlimited。Rust 端 `max_bytes.map(|v| v as i64).unwrap_or(-1)`。

**理由**：z42 long 是有符号 i64；`u64::MAX` 溢出，`0` 容易与"limit=0 字节"
混淆，`-1` 唯一无歧义可用 sentinel。Phase 3 引入 `Optional<T>` 后再迁移。

### Decision 7: WeakHandle 保留还是删除

**问题**：GCHandle 引入后 WeakHandle 是否冗余？

**决定**：保留作低级 primitive。

**理由**：
- C# 双层结构：`WeakReference`（轻量、自动）+ `GCHandle`（强力、需 Free）
  并存——目的在不同
- 现有 `Delegates/SubscriptionRefs.z42` 内部 weak-only 场景用 WeakHandle
  比"AllocWeak 再记得 Free"自然
- WeakHandle 本身是 1 字段 wrapper、零维护成本

### Decision 8: WeakHandle 与 GCHandle.AllocWeak 是否共享 backing

**问题**：GCHandle.AllocWeak 内部要复用 WeakHandle.MakeWeak，还是独立 corelib path？

**决定**：独立。两条 path 都走 Rust 端 `Weak<RefCell<Object>>`，但
- WeakHandle 是 z42 class，含 `Weak<RefCell<Object>>` 直接持有
- GCHandle.AllocWeak 走 HandleTable slot，slot 内是 `HandleEntry::Weak(Weak<...>)`

**理由**：WeakHandle slot 不需 Free（Rc 自动管理），HandleTable slot 需 Free
（slab 管理）。混用会让 GCHandle.Free 行为奇怪（"释放 WeakHandle 字段"无意义）。
独立 path 语义清晰，代码也分离。

## Implementation Notes

### GCHandle.z42 骨架

```z42
namespace Std;

public enum GCHandleType {
    Weak,
    Strong,
}

public struct GCHandle {
    private long _slot;

    [Native("__gc_handle_alloc")]
    public static extern GCHandle Alloc(object target, GCHandleType type);

    public static GCHandle AllocWeak(object target)   { return Alloc(target, GCHandleType.Weak); }
    public static GCHandle AllocStrong(object target) { return Alloc(target, GCHandleType.Strong); }

    [Native("__gc_handle_target")]    public extern object        Target      { get; }
    [Native("__gc_handle_is_alloc")]  public extern bool          IsAllocated { get; }
    [Native("__gc_handle_kind")]      public extern GCHandleType  Kind        { get; }
    [Native("__gc_handle_free")]      public extern void          Free();
}
```

### HeapStats.z42 骨架

```z42
namespace Std;

public class HeapStats {
    public long Allocations       { get; }
    public long GcCycles          { get; }
    public long UsedBytes         { get; }
    public long MaxBytes          { get; }   // -1 = unlimited
    public long RootsPinned       { get; }
    public long FinalizersPending { get; }
    public long Observers         { get; }

    // ctor 由 corelib builtin 直接 emit Value::Object，不需要脚本端 ctor
    // 如果 z42 当前 emit 路径需要 ctor sigprint，写一个 internal 7-arg ctor
}
```

### Rust HandleTable trait API

```rust
pub trait MagrGC {
    // ... existing ...

    fn handle_alloc(&self, target: &Value, kind: GcHandleKind) -> u64;
    fn handle_target(&self, slot: u64) -> Option<Value>;
    fn handle_is_alloc(&self, slot: u64) -> bool;
    fn handle_kind(&self, slot: u64) -> Option<GcHandleKind>;  // None if not allocated
    fn handle_free(&self, slot: u64);
}

pub enum GcHandleKind {
    Weak,
    Strong,
}
```

### RcMagrGC 实现

```rust
struct HandleSlab {
    entries: Vec<Option<HandleEntry>>,  // index 0 reserved
    free_list: Vec<u64>,
}

enum HandleEntry {
    Strong(Rc<RefCell<Object>>),
    Weak(Weak<RefCell<Object>>),
}

impl HandleSlab {
    fn alloc(&mut self, value: &Value, kind: GcHandleKind) -> u64 {
        let entry = match (value, kind) {
            (Value::Object(rc), GcHandleKind::Strong) => Some(HandleEntry::Strong(rc.clone())),
            (Value::Object(rc), GcHandleKind::Weak)   => Some(HandleEntry::Weak(Rc::downgrade(rc))),
            (Value::Array(rc),  GcHandleKind::Strong) => Some(HandleEntry::Strong(rc.clone())),
            (Value::Array(rc),  GcHandleKind::Weak)   => Some(HandleEntry::Weak(Rc::downgrade(rc))),
            // atomic values: weak fails, strong store as Rc-wrap? No — atomic is not Rc.
            // Decision 5: AllocWeak(atomic) → slot 0; we won't add to slab.
            // AllocStrong(atomic) — also slot 0 (atomic doesn't need anchoring; just hold the Value)
            // Actually we need to think about this; design says atomic strong should work...
            _ => None,
        };
        if entry.is_none() { return 0; }
        // ... pop from free_list or push new slot, return slot id ...
    }
}
```

**待 spike 验证**：atomic value + Strong 应不应 anchor？保留原 Value clone
存进 slab？暂定**支持**（slab entry 改 `Strong(Value)` 而非 `Strong(Rc<...>)`，
反正 Strong 语义就是"持值不释放"，无所谓 RC 还是 atomic）。

### corelib/gc.rs 6 个新 builtin

```rust
fn builtin_gc_handle_alloc(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let target = args[0].clone();
    let kind = match args[1] { Value::I64(0) => GcHandleKind::Weak, _ => GcHandleKind::Strong };
    let slot = ctx.heap().handle_alloc(&target, kind);
    // emit GCHandle struct: { _slot: slot }
    Ok(Value::Object(make_struct(&[("_slot", Value::I64(slot as i64))])))
}

fn builtin_gc_handle_target(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let slot = extract_slot(&args[0])?;
    Ok(ctx.heap().handle_target(slot).unwrap_or(Value::Null))
}

// ... is_alloc / kind / free 类似 ...

fn builtin_gc_stats(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let stats = ctx.heap().stats();
    Ok(Value::Object(make_struct(&[
        ("Allocations",       Value::I64(stats.allocations as i64)),
        ("GcCycles",          Value::I64(stats.gc_cycles as i64)),
        ("UsedBytes",         Value::I64(stats.used_bytes as i64)),
        ("MaxBytes",          Value::I64(stats.max_bytes.map(|v| v as i64).unwrap_or(-1))),
        ("RootsPinned",       Value::I64(stats.roots_pinned as i64)),
        ("FinalizersPending", Value::I64(stats.finalizers_pending as i64)),
        ("Observers",         Value::I64(stats.observers as i64)),
    ])))
}
```

**关键**：how does corelib emit struct GCHandle / class HeapStats from Rust
side? 复用现有"native ctor returns ScriptObject"路径——但 GCHandle 是 struct，
HeapStats 是 class，两条路径可能不同。spike 第一项要验证。

## Testing Strategy

### Phase A spike（tasks.md 第一项）

写最小 source.z42：

```z42
struct H { long _slot; [Native("__gc_handle_alloc")] public static extern H Make(); }
public static void Main() { var h = H.Make(); Console.WriteLine(h._slot); }
```

确认 IRgen + VM 能：
- 解析 struct 含 extern 静态方法
- corelib 返回的 Value::Object（含 _slot 字段）能赋给 struct 局部变量
- 字段访问读出 long

如果某项不支持，回到 design.md 调整方案。

### Unit tests Rust

- HandleTable.alloc 返回非 0 slot ID（reserved 0 除外）
- alloc 后 is_alloc=true，free 后 is_alloc=false
- Strong slot anchor：alloc → drop external Rc → is_alloc=true、target=Some
- Weak slot 自动 clear：alloc → drop external Rc → target=None、is_alloc=true
- free 后再 alloc 复用 slot（free_list 工作）
- atomic value AllocWeak 返回 slot=0、IsAllocated=false

### Golden tests

- `111_gc_handle/`：strong 与 weak 端到端（构造、Target 读、Free、struct copy）
- `112_gc_stats/`：分配 N 个对象，GetStats 字段一致性

### 现有回归

`./scripts/test-vm.sh` 全绿（包括 `110_gc_cycle` 等老 GC 测试）。

## Risk

- **Risk 1**：z42 struct + extern static + private long field 全链路未验证
  → spike 阶段 A 提前发现；如失败回到 class 形态（牺牲 struct 共享语义）
  → User 裁决
- **Risk 2**：corelib 返回 struct value 的 Value 形态：是 `Value::Object(Rc<...>)`
  with struct fields，还是 unboxed？z42 当前 struct 实现细节未知
  → spike 同时验证；如果只能用 Rc，那 GCHandle struct 的"value semantics"在
  corelib boundary 失效，但拷贝仍共享 _slot（ID 是值），效果相同
- **Risk 3**：Phase 1 RC 没法跑 collect 触发 Strong slot anchor（GC.ForceCollect
  对 Rc::clone 持有的对象本就不会回收）→ Strong 测试用"drop external var
  + GC.ForceCollect 后 Target 仍非 null"验证（间接证明 anchor 工作）
