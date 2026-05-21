# Spec: Mark-sweep GC

## ADDED Requirements

### Requirement: Mark phase walks all reachable Values from roots

#### Scenario: Mark from VmContext roots
- **WHEN** `collect_cycles` invoked
- **THEN** mark phase BFS from all roots provided by the external root
  scanner (VmCore.static_fields + every VmContext's pending_exception /
  call_stack frames' regs+env_arena / func_ref_slots / pinned roots)
- **AND** every reachable `GcAllocation` has its `marked = 1` after the
  walk

#### Scenario: Cyclic references reachable iff some root reaches them
- **WHEN** a Value::Object A references B which references A (cycle); no
  root reaches A
- **THEN** neither A nor B is marked → both swept in the sweep phase
  (this is the case trial-deletion needed elaborate tentative counts for;
  mark-sweep handles it naturally)

### Requirement: Sweep phase drops unmarked objects

#### Scenario: Unmarked allocations are removed from registry + Drop'd
- **WHEN** sweep phase iterates the registry
- **THEN** for each `Arc<GcAllocation<T>>` with `marked == 0`:
  - Registry entry is removed (registry holds the only "system" Arc)
  - If user code holds no other Arc, the Arc drops → T's destructor runs
    (finalizer fires via existing GcAllocation::Drop path)
  - `freed_bytes` accumulator increments by the object's reported
    `object_size_bytes`
- **AND** marked objects keep their entries; mark bit is RESET to 0 for
  the next cycle

### Requirement: Drop semantics unchanged for non-cycle paths

#### Scenario: Last strong reference drops releases immediately
- **WHEN** all `Arc<GcAllocation<T>>` clones to an object reach zero
  (no cycle; user simply released the last reference)
- **THEN** Arc's natural Drop runs immediately; T's destructor fires;
  registry entry is cleared on next collect or via the existing
  alloc-time housekeeping (TBD in design.md Decision 4)

### Requirement: Stats accuracy preserved

#### Scenario: gc_cycles increments per collect_cycles call
- **WHEN** `collect_cycles` is called
- **THEN** `HeapStats.gc_cycles` increments by 1 (same behavior as
  pre-spec)

#### Scenario: freed_bytes reflects swept objects
- **WHEN** sweep phase reclaims N bytes worth of objects
- **THEN** `HeapStats.freed_bytes` accumulator grows by N; subsequent
  `force_collect()` return value matches

## MODIFIED Requirements

### Requirement: collect_cycles algorithm

**Before:** Trial-deletion (Bacon-Rajan simplified):
1. Mark reachable from roots
2. Snapshot alive set from registry
3. Filter unreachable = alive ∖ reachable
4. Tentative deletion: subtract internal references
5. Break cycles where tentative count reaches 0
6. Return as alive_vec drops + Arc chain releases

**After:** Standard mark-sweep:
1. Mark phase: BFS from roots, set `marked = 1` on every reachable object
2. Sweep phase: walk registry; for each `marked == 0`, remove from
   registry + accumulate freed_bytes; reset `marked = 0` on marked
   objects (prep for next cycle)

## IR Mapping

No new IR. GC algorithm internal to the runtime.

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen — 无变更
- [x] VM interp — GC algorithm rewrite; no opcode/dispatch change
- [x] VM JIT — same (calls heap.collect_cycles via the same trait)
