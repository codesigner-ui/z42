# GC Handle (Std.GCHandle) — Implementation Principles

> Spec: `spec/archive/2026-05-07-reorganize-gc-stdlib/`. 本文记录设计原理（数据
> 结构、anchor 语义、与 C# `System.Runtime.InteropServices.GCHandle` 的对应、
> Phase 3 扩展点），让接手者不必读源码就能理解"为什么这样设计"。

## 一句话

`Std.GCHandle` 是 1 字段 struct（`_slot: long`），背后是 corelib `HandleTable`
slab —— **拷贝句柄共享同一 slot，Free 后所有 alias 同步失效**，与 C#
`GCHandle` struct 完全等价（C# 是 IntPtr → CLR handle table；z42 是 long →
Rust `HandleTable`）。

## 用户视角

```z42
// 强引用 anchor 目标到指定 scope
GCHandle h = GCHandle.AllocStrong(target);
// ... target 哪怕作用域外也保持活
h.Free();   // 显式释放 anchor，target 现可被 GC 回收

// 弱引用 + 显式 Free
GCHandle w = GCHandle.AllocWeak(target);
if (w.Target != null) { /* 还活着 */ }
w.Free();

// struct copy = 共享 backing slot
GCHandle h2 = h;            // 同一 _slot
h.Free();                   // h2.IsAllocated 也变 false
```

| API | 等价 C# |
|------|--------|
| `GCHandle.Alloc(target, GCHandleType.Strong)` | `GCHandle.Alloc(o, GCHandleType.Normal)` |
| `GCHandle.AllocWeak(target)` | `GCHandle.Alloc(o, GCHandleType.Weak)` |
| `GCHandle.Target { get; }` | `GCHandle.Target { get; set; }`（**z42 只读**：避免 anchor 切换的复杂性，先小步走）|
| `GCHandle.IsAllocated` | `GCHandle.IsAllocated` |
| `GCHandle.Kind` | 派生自 `GCHandleType`（C# 没有直接 query，z42 加之）|
| `GCHandle.Free()` | `GCHandle.Free()` |

## 选型决策

### 为什么是 struct + corelib backing 而不是 class？

C# `GCHandle` 是 struct，单字段 IntPtr 指向 CLR 内部 handle table。这种设计
让 "拷贝句柄共享 backing" 成为自然结果——多个 alias 共享同一个 slot，Free
影响所有 alias。

z42 端如果把 GCHandle 做成纯 stdlib class（reference type），class 实例自然
共享。但实际上 corelib 端不支持 z42 class 实例（z42 class instance 是
`Value::Object(GcRef<ScriptObject>)`），corelib 不容易直接持有 z42 用户类
的引用。

如果把 GCHandle 做成纯 stdlib struct（value type）但**不**走 corelib
backing，拷贝时字段全部 clone，每个 alias 都"独立"——`h1.Free()` 只会清空
h1 的字段，h2 仍持有 weak/strong 引用。这违反 C# 风格，让 Free 失去意义。

→ 选 struct + corelib backing：z42 struct 单字段 `_slot: long` 是 slot ID，
拷贝复制 ID（值类型），所有 alias 通过同一 ID 访问 corelib 中的同一 slot。
Free 操作 corelib slot，影响所有 alias。

### 为什么用 slab + free list？

| 方案 | 评估 |
|------|------|
| `HashMap<u64, HandleEntry>` + 单调递增计数器 | 平均 O(1)，但堆碎片；slot 不复用 |
| **`Vec<Option<HandleEntry>>` slab + `Vec<u64>` free list** | O(1) alloc/free + slot 复用紧凑 |

slab 简单、cache-friendly。`free_list` 让 `Free` 后的 slot 立即进入待复用
池，避免 vec 单调增长。Slot ID 0 reserved 作 "未分配" sentinel
（`entries[0]` 永不读写）。

### Strong / Weak 的物理区分

```rust
enum HandleEntry {
    StrongObject(GcRef<ScriptObject>),  // Rc::clone of the Object
    StrongArray (GcRef<Vec<Value>>),
    StrongAtomic(Value),                 // Atomic Value clone (I64/Str/Bool/...)
    WeakObject  (WeakGcRef<ScriptObject>),
    WeakArray   (WeakGcRef<Vec<Value>>),
}
```

**Strong**：slab 持 `Rc::clone` —— anchor 目标，因为 z42 RC 模式下 Rc count
保持 ≥ 1 时对象不会被回收（即使所有外部 Rc clone 都 drop）。Atomic 值（I64
/ Str / Bool / ...）不是 Rc-backed，slab 直接 clone Value 存进去。

**Weak**：slab 持 `WeakGcRef`（`Rc::downgrade`）—— 不增加 Rc count，目标
被外部回收后 `WeakGcRef.upgrade()` 返回 None。Atomic 值不能 weak-ref，
`AllocWeak(atomic)` 在 `handle_alloc` 层返回 slot=0（IsAllocated=false）。

### atomic 值的 AllocWeak 行为

```z42
GCHandle h = GCHandle.AllocWeak(42);
h.IsAllocated;  // false（atomic 不可弱化）
```

与既有 `WeakHandle.MakeWeak(atomic) → null` 行为一致。**不**抛异常——用户用
`if (h.IsAllocated)` 判断比 try/catch 更顺手；C# 也是 "Alloc 总能成功，
Target/IsAllocated 后查"。

## 与 `Std.WeakHandle` 的关系

| 需求 | 选 | 理由 |
|------|----|------|
| 简单弱引用，靠 GC 自动清理 | `WeakHandle` | 轻量、无 Free、`Delegates/SubscriptionRefs.z42` 内部 weak-only 已用 |
| 强引用 + 显式 Free 控制释放点 | `GCHandle` (Strong) | C# `GCHandle.Normal` 等价 |
| 弱引用 + 显式 Free（slot 复用） | `GCHandle` (Weak) | 显式控制比自动 GC 更准 |
| 在 Strong / Weak 模式间切换 | `GCHandle` | Kind 读取后 Free + 重 Alloc |
| 跨 native 边界 anchor 对象 | `GCHandle` (Strong) | C# FFI 主用例 |

两者**独立**的 corelib path，不互相依赖：

- `WeakHandle` 是 z42 class，`NativeData::WeakRef(weak)` 直接持有 `WeakRef`
- `GCHandle` 是 z42 struct，`_slot` 通过 corelib `HandleTable` slot 间接持有

混合一条 path（`GCHandle.AllocWeak` 内部 wrap WeakHandle）会让"GCHandle.Free
释放 wrapper 不释放 backing weak"语义混乱；独立 path 各自简洁。

## HeapStats sentinel —— 为什么 `MaxBytes = -1`？

Rust `HeapStats.max_bytes: Option<u64>`，z42 当前无 `Optional<T>`。三个候选
sentinel：

| sentinel | 缺点 |
|----------|------|
| `0` | 容易与"limit=0 字节"实际值混淆 |
| `u64::MAX` | z42 long 是 i64，`u64::MAX` 转 i64 溢出 |
| **`-1`** | 唯一无歧义可用值 |

Phase 3 引入 `Optional<long>` 后再迁移到 nullable 形态。

## Phase 3+ 扩展规划

| 扩展项 | 触发条件 | 实施位置 |
|--------|---------|---------|
| `GCHandleType.Pinned` | tracing GC 引入对象移动 → pinning 防止 relocate 才有意义 | `HandleEntry::Pinned` + `handle_alloc` 验证 + Phase 3 pin 协议 |
| `GCHandleType.WeakTrackResurrection` | finalizer 真触发后 → "weak + finalizer-aware" 才有意义 | `HandleEntry::WeakResurrect` + 与 finalizer drop 顺序对齐 |
| `GCHandle.AddrOfPinnedObject()` → `IntPtr` | Pinned 引入后跨 native 边界传指针 | 配合 Pinned 同期落地 |
| `GCHandle.Target` setter | 用户反馈"想原地切 strong→weak 而不 Free + 重 Alloc" | 加 `handle_set_target` builtin + setter |

新增枚举值时 `HandleEntry` 的 Rust enum 加 variant，z42 端 `GCHandleType` 加
值——既有用户代码兼容（match 都需要补分支，但用户不写 match）。

## 实现位置

| 层 | 文件 | 职责 |
|----|------|------|
| Script | `src/libraries/z42.core/src/GC/GCHandle.z42` | struct + enum + extern declarations |
| Script | `src/libraries/z42.core/src/GC/HeapStats.z42` | 7 long auto-property class |
| Script | `src/libraries/z42.core/src/GC/GC.z42` | 加 `GetStats()` 静态方法 |
| Corelib | `src/runtime/src/corelib/gc.rs` | 6 个 builtin（5 GCHandle + 1 stats）+ TypeDesc 工厂 |
| GC trait | `src/runtime/src/gc/heap.rs` | `handle_alloc` / `_target` / `_is_alloc` / `_kind` / `_free` |
| GC impl | `src/runtime/src/gc/rc_heap.rs` | `HandleSlab` + `HandleEntry` enum + RcMagrGC impl |
| GC types | `src/runtime/src/gc/types.rs` | `GcHandleKind` enum |
| Dispatch | `src/runtime/src/corelib/mod.rs` | 6 个 builtin 注册 |
| Tests | `src/runtime/src/gc/rc_heap_tests.rs` | 10 个 HandleTable 单测 |
| Tests | `src/tests/gc/113_gc_handle/` | strong / weak / Free / struct copy e2e |
| Tests | `src/tests/gc/114_gc_stats/` | GetStats 字段一致性 e2e |
