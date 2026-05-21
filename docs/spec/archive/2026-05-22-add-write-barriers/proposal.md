# Proposal: GC Write Barriers (call-site wiring)

## Why

`MagrGC` trait already declares two write-barrier hooks
([`src/runtime/src/gc/heap.rs:120-123`](../../../../src/runtime/src/gc/heap.rs#L120-L123)):

```rust
fn write_barrier_field(&self, _owner: &Value, _slot: usize, _new: &Value) {}
fn write_barrier_array_elem(&self, _arr: &Value, _idx: usize, _new: &Value) {}
```

Both default to no-op. The intent (per vm-architecture.md Phase notes
and the existing `gc/heap_tests.rs` no-op assertions) is "Phase 2+ uses
this for generational / SATB; Phase 1 stays no-op". **But the call
sites are not wired** — interp & JIT mutate heap slots directly without
notifying the GC.

This is half-finished infrastructure: future `add-concurrent-gc` (A4)
and `add-generational-gc` (A3) both require the barrier dispatch path
to exist so they can override and react to writes. Without barrier
call sites wired, the next concurrent / generational spec would have
to do *both* "add barriers" and "make them useful" in one commit — too
broad for safe review.

Wire the call sites now while the GC is quiescent (STW mark-sweep, just
landed). The change is pure infrastructure: behavior unchanged because
the trait method is still a no-op default. Future specs override the
defaults; call sites stay put.

## What Changes

- Insert `ctx.heap().write_barrier_field(owner, slot, new_value)` calls
  after every heap field write in interp + JIT
- Insert `ctx.heap().write_barrier_array_elem(arr, idx, new_value)`
  calls after every heap array element write in interp + JIT
- Add a thin observer-pattern test fixture so we can prove the calls
  fire from each insertion site without standing up a real generational
  collector
- Document barrier contract in `vm-architecture.md` (when callers must
  invoke, what GC implementations can assume)

**No behavior change**: trait defaults remain no-op; all existing GC
tests stay GREEN; performance impact ≤ one virtual call per heap write
(measured in P2 of this spec).

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/types.rs` | MODIFY | Add `Value::is_heap_ref(&self) -> bool` inherent method (call-site filter — design.md Decision 1) |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | Wire `if v.is_heap_ref() { write_barrier_field(...) }` after `borrowed.slots[slot] = v;` (FieldSet, 2 places — IC fast + slow path) |
| `src/runtime/src/interp/exec_array.rs` | MODIFY | Wire `write_barrier_array_elem` after `borrowed[i] = v;` (ArraySet) |
| `src/runtime/src/interp/exec_object.rs` | MODIFY | StaticSet — static fields are heap roots but not "inside a heap object"; barrier elided (see design.md Decision 2) |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | `jit_field_set` — wire barrier call after the slot write |
| `src/runtime/src/jit/helpers/array.rs` | MODIFY | `jit_array_set` — wire barrier call after element write |
| `src/runtime/src/gc/heap.rs` | MODIFY | Tighten doc on the two trait methods — when callers invoke, what overrides can assume |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Add optional barrier counter (test-only, behind `#[cfg(test)]`) — counts how many times each barrier fires for the observer fixture |
| `src/runtime/src/gc/arc_heap_tests/write_barriers.rs` | NEW | Unit tests verifying barrier fires from FieldSet / ArraySet (interp + JIT) |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register the new `write_barriers` module |
| `src/runtime/benches/gc_cycle_bench.rs` | MODIFY | Add `barrier_overhead` workload: 1M field writes with barrier no-op vs hypothetical `unsafe { write_unchecked }` baseline (proves <1ns barrier cost) |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Add "Write barrier contract" section under GC chapter — semantics, call sites, future use |

**只读引用**（理解上下文必须读，但不修改）：

- `src/runtime/src/metadata/types.rs` — Value variant enumeration (to confirm primitive vs heap discriminator)
- `src/runtime/src/gc/heap_tests.rs` — existing no-op assertions (kept as-is; we add new tests, don't modify)

## Out of Scope

- 实际的 generational / concurrent / SATB 实现 — 在 `add-generational-gc` / `add-concurrent-gc` 单独 spec 落地
- Compiler-side barrier elision (codegen 已知 field 是 primitive type 时省略 barrier 调用) — 后续 perf spec，需要先有真实 barrier overhead 测量
- Read barriers — Z42 GC 模型当前不需要（只 mark-sweep STW）；未来若引入 Brooks / forwarding pointer GC 再开
- Stack write barriers — stack 本来就是 root，每个 collect 都全扫，无需 barrier
- 移除 `MagrGC` trait method 的 `_` 参数 underscore 前缀（标识"未使用"）— 调用 site 落地后参数变成"实际使用"，可以一并去掉前缀；属于本 spec 范围内的自然清理

## Open Questions

无 — 设计已在 mark-sweep spec 落地时充分讨论。这是 follow-up 实施。
