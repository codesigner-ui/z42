# Design: GC Write Barriers (call-site wiring)

## Architecture

```
                  ┌──────────────────────────┐
   interp/jit ──► │ MagrGC trait method      │ ─► default no-op
   write site     │ write_barrier_field      │    (Phase 1: STW mark-sweep)
                  │ write_barrier_array_elem │
                  └──────────────────────────┘
                              ▲
                              │  (Phase 2+: overrides)
                              │
                  ┌──────────────────────────┐
                  │ Generational / SATB /    │
                  │ Concurrent backends      │
                  └──────────────────────────┘
```

Single chokepoint per write kind. The barrier dispatch is a virtual
call into the trait method; the cost on the no-op default is one
indirect call (~2-4 ns on M-series under release). Future overrides
read `&Value` and can branch on `matches!(new, Value::Object(_) | ...)`
to skip work for primitive writes.

## Decisions

### Decision 1: 在 call site 过滤 primitive 写入

**问题**: 应该在 interp / JIT 调用 barrier 前先 `if new.is_heap_ref() { ... }`
过滤，还是无条件调用让 trait 实现自己决定？

**选项**:
- A — Call site guard: `if new.is_heap_ref() { heap.write_barrier_field(...) }`。
  Primitive 写入连虚函数 dispatch 都省，no-op 默认下 cost ≈ 一次 enum
  discriminant 比较（~1 ns）。每个调用点多一行 match，被 `Value::is_heap_ref()`
  inherent 方法统一收口。
- B — 无条件调用，trait 实现自己 match: 调用点干净（一行），但每次 primitive
  写入也付一次虚函数 call (~2-4 ns)。Phase 2+ 的 generational 实现可能
  想观察"primitive write 也算 dirty card"，但实际不需要 —— primitive 写入
  既不改变 cross-region 引用关系也不改变 cross-generation 引用关系（slot
  原本指 heap → 现在指 primitive：被指向的对象不再可达*经过这个 slot*，
  下次 GC 自然处理；slot 原本指 primitive → 仍指 primitive：完全无关）。

**决定**: A。Primitive 写入在语义上不可能触发 card-mark 也不可能触发
SATB 关心的情况，barrier dispatch 是纯浪费。在 call site 过滤是**正确的**
实现 —— 不只是优化。

实施形态：
- 新增 `Value::is_heap_ref(&self) -> bool` inherent 方法（与 P1 mark-sweep
  spec 加的 `trace_children` 平行，单一真相来源）
- Call site 模板：
  ```rust
  borrowed.slots[slot] = v.clone();
  drop(borrowed);
  if v.is_heap_ref() {
      ctx.heap().write_barrier_field(&owner_value, slot, &v);
  }
  ```
- Trait method default 仍 no-op（不在 trait 层重复 filter；override impls
  可以 `debug_assert!(new.is_heap_ref())` 作为契约 sanity check）

**契约**：Caller 保证 barrier 只在 heap-ref 写入时调用，trait override 无需
再 filter。这条规则写入 vm-architecture.md "Write barrier contract" 段。

### Decision 2: StaticSet 不加 barrier

**问题**: `StaticSet` 把值写入 `VmContext::static_fields` Vec — 这是
heap-living state，要不要加 barrier？

**选项**:
- A — 加：静态字段也是 mutation，对称
- B — 不加：static_fields 已经是 GC root（每次 collect 由
  `external_root_scanner` 全扫），不存在 "old object → new object 的
  跨代 / cross-region 写" 这种 barrier 关心的情况

**决定**: B。static_fields 是 roots，barrier 设计的"跨代写"语义对 root
无意义（root 永远在最年轻的代里 by definition）。proposal Scope 表把
exec_object.rs 列了两次但 StaticSet 那行是"barrier 不加"的标注；实际
StaticSet path 不改动。

### Decision 3: 调用 barrier 的顺序：write-then-call vs call-then-write

**问题**: barrier call 应该在 slot/elem 写**之前**还是**之后**？

**选项**:
- A — call-then-write (pre-barrier / SATB-friendly): barrier 看见旧值。
  Concurrent GC 中 SATB (Snapshot-At-The-Beginning) 算法需要：被覆盖
  的旧引用要先入 mark queue 再覆盖，防止"还没扫到就丢失"的对象漏标
- B — write-then-call (post-barrier / Card-marking-friendly): barrier 看见新值。
  Generational GC 中 card marking 需要：old-gen 写到 young-gen 时，
  card 表标 dirty，下次 minor GC 扫这些 card 当 root

**决定**: B (write-then-call)。z42 的中期目标是 generational（A3），
A3 用 card marking 比 SATB 自然。SATB 是 concurrent (A4) 的考虑，
若 A4 落地确实需要 pre-barrier，再扩 trait 加 `write_barrier_field_pre`
（携带 old value 参数），不强迫 A3 也付 pre-barrier 代价。
Trade-off 文档化在 vm-architecture.md "Write barrier contract" 段。

### Decision 4: Test observer 通过 ArcMagrGC 字段而非 trait method

**问题**: 怎么验证 barrier 真的被调用？

**选项**:
- A — Trait override: 写一个 `TestGC: MagrGC` 实现 + override barrier
  方法 → 但要复刻 ArcMagrGC 全部其他 trait 方法
- B — ArcMagrGC 内部字段: 加 `barrier_observer: Mutex<Option<Box<dyn Fn(...)>>>`
  `#[cfg(test)]` 字段；trait 实现里调用 observer 然后 fall through 到默认 no-op

**决定**: B。`#[cfg(test)]` 隔离，0 production overhead；不需要复刻 trait；
测试 mock 隔离干净 (一个 ArcMagrGC 实例配一个 observer，drop 时自动清掉)。
Phase 2+ 的真实 generational/concurrent 实现可以直接在 ArcMagrGC trait
override 里放真实逻辑（不依赖 observer）。

### Decision 5: IC fast path也调用 barrier

**问题**: FieldSet 有 IC fast path（缓存 type id + slot → 直接写）。
要不要在 fast path 也加 barrier？

**选项**:
- A — Fast path 跳过 barrier，slow path 加：fast path 保持 minimum overhead
- B — Fast path / slow path 都加：语义对等

**决定**: B。fast path 跳过会让 "barrier 调用次数" 取决于 IC 命中率，
对未来 generational / concurrent 实现是 dirty correctness bug（漏了
fast path 的 mutation → mark queue 不完整 → use-after-free）。
这条规则在 vm-architecture.md "Write barrier contract" 段写明：
**every** heap write must dispatch barrier，不分 fast/slow。

## Implementation Notes

### Interp wiring 三处（含 Decision 1 的 primitive filter）

1. `exec_object.rs:172` — IC fast path 写入后
   ```rust
   borrowed.slots[slot] = v.clone();
   drop(borrowed);  // release lock before barrier call (avoids re-entrance)
   if v.is_heap_ref() {
       ctx.heap().write_barrier_field(&owner_value, slot, &v);
   }
   ```
   注：调用 barrier 时不能持有 borrowed (Mutex/RefCell) lock，否则
   generational / concurrent 实现里若 barrier 想访问 owner.slots[k] 会 deadlock。
   Decision 5 要求 fast path 也加 dispatch → 这里需要把 owner 提到外面 clone 一次
   (Object 是 GcRef = Arc clone，cheap)。

2. `exec_object.rs:189` — IC slow path 写入后（同上）

3. `exec_array.rs:55` — ArraySet 写入后
   ```rust
   borrowed[i] = v.clone();
   drop(borrowed);
   if v.is_heap_ref() {
       ctx.heap().write_barrier_array_elem(&arr_value, i, &v);
   }
   ```

### JIT wiring 两处

1. `jit_field_set` (helpers/object.rs:196-238) — 在 `b.slots[slot] = v` 之后加：
   ```rust
   drop(b);
   if v.is_heap_ref() {
       ctx.heap().write_barrier_field(&owner, slot, &v);
   }
   ```
2. `jit_array_set` (helpers/array.rs:70) — 同样模式

### Value::is_heap_ref 实现

```rust
// metadata/types.rs Value impl
#[inline]
pub fn is_heap_ref(&self) -> bool {
    matches!(self,
        Value::Object(_) | Value::Array(_) | Value::Closure { .. } |
        Value::Ref { .. } | Value::WeakRef(_)
    )
}
```
配合 mark-sweep spec P1 加的 `trace_children`：两者覆盖 "Value 是否含 GC
追踪关心的内容" 这一个语义，互为对偶（is_heap_ref 是判定，trace_children
是遍历）。

### Test observer 设计

```rust
// 在 ArcMagrGC inner 加 #[cfg(test)] 字段：
#[cfg(test)]
pub(crate) barrier_observer: parking_lot::Mutex<Option<BarrierObserver>>,

// type
#[cfg(test)]
pub type BarrierObserver = Box<dyn Fn(BarrierEvent) + Send + Sync>;

#[cfg(test)]
pub enum BarrierEvent {
    Field { owner_addr: usize, slot: usize, new_is_heap: bool },
    ArrayElem { arr_addr: usize, idx: usize, new_is_heap: bool },
}

// 在 write_barrier_field / _array_elem impl 里 #[cfg(test)] 时 fire observer
```

### Doc 段落（vm-architecture.md 加入）

新增 "Write barrier contract" sub-section under GC 章：
- 调用点：interp + JIT 的 FieldSet / ArraySet (post-write, post-lock-release)
- 默认实现：no-op (mark-sweep STW 无需)
- 后续 backend 用法：Generational 用 card marking (look at `new`);
  Concurrent 若用 SATB 需要扩 trait 加 pre-barrier 方法

## Testing Strategy

- **Unit**: `arc_heap_tests/write_barriers.rs` 5+ tests:
  - FieldSet → field barrier fires (interp path via direct exec_object call)
  - ArraySet → array barrier fires
  - IC fast path → barrier fires (same count as slow path on cold IC)
  - Default no-op preserves behavior: 跑现有 cycle_collection tests with
    observer 安装 (验证 GC tests 不依赖 barrier 暗中影响)
  - Observer install/remove leak test: 多个 ArcMagrGC 实例独立
- **JIT**: 现有 JIT smoke tests (`scripts/test-vm.sh` JIT mode) 必须 GREEN，
  外加在 JIT helpers 中加一个单测验证 `jit_field_set` 调 barrier
- **End-to-end**: `test-all.sh --scope=full` 必须全绿且字节相同输出
- **Bench**: `gc_cycle_bench.rs` 加 `barrier_overhead` workload：
  hot loop 做 1M `obj.field = i` writes，跑两次（barrier on / barrier off via 
  build feature flag），report overhead 增量

## Deferred / Future Work

### add-generational-gc (A3)
- 利用 barrier 实现 card marking + young-gen rapid collect

### add-concurrent-gc (A4)
- 利用 barrier (可能扩 trait 加 pre-barrier) 实现 SATB / incremental update

### barrier-codegen-elision (perf spec, 待估)
- IR codegen 在 emit FieldSetInstr 时若 field type 是 primitive，可以
  emit 一条新指令 `FieldSetPrim` (不触发 barrier)，节省虚函数调用
- 预估收益：取决于 P2 bench；若 barrier 占 hot path > 10%，开此 spec
