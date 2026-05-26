# Spec: gc-softhandle

## ADDED Requirements

### Requirement: SoftHandle creation and basic Get

#### Scenario: Create soft handle, get target before any GC
- **WHEN** `SoftHandle h = SoftHandle.Create(obj)` is called with a live object
- **THEN** `h.Get()` returns the same object (non-null)

#### Scenario: Get returns null after explicit GC when no strong ref + pressure
- **WHEN** a soft handle is the only reference to an object AND the heap is at or above the soft-pressure threshold (simulated via `GC.SetMaxHeapBytes + strict OOM`)
- **THEN** after `GC.ForceCollect()`, `h.Get()` returns null

#### Scenario: Get returns non-null after GC when heap is below pressure threshold
- **WHEN** a soft handle is the only reference to an object AND `max_heap_bytes == 0` (unlimited heap)
- **THEN** after `GC.ForceCollect()`, `h.Get()` still returns the original object

#### Scenario: Create with null target
- **WHEN** `SoftHandle.Create(null)` is called
- **THEN** the resulting `SoftHandle.Get()` immediately returns null (same as WeakHandle.MakeWeak behavior)

### Requirement: Soft handle cleared under memory pressure

#### Scenario: Soft handles cleared when heap full (strict OOM mode)
- **WHEN** `GC.SetMaxHeapBytes(limit)` is set to a small value AND a soft-only object is allocated AND heap fills toward limit
- **THEN** `GC.ForceCollect()` clears the soft handle (Get returns null) once `used_bytes / max_bytes >= 0.80`

#### Scenario: Strong ref prevents clearing even under pressure
- **WHEN** both a strong local variable AND a soft handle reference the same object AND pressure > threshold
- **THEN** after `GC.ForceCollect()`, `h.Get()` still returns non-null (strong ref prevents collection)

### Requirement: Multiple soft handles to same object

#### Scenario: All soft handles to an object are cleared together
- **WHEN** two `SoftHandle`s reference the same object, no strong ref exists, pressure > threshold
- **THEN** both `h1.Get()` and `h2.Get()` return null after `GC.ForceCollect()`

### Requirement: SoftHandle does not prevent GC from collecting when pressure exceeds threshold

#### Scenario: Soft handle is not a root under pressure
- **WHEN** heap pressure >= threshold
- **THEN** objects reachable ONLY through soft handles may be swept in the same GC cycle as otherwise unreachable objects

## Pipeline Steps

Affected pipeline stages (runtime only — no compiler change):

- [ ] VM interp (revive pass in GC collect)
- [ ] Corelib builtins (`__soft_handle_create`, `__soft_handle_get`)
- [ ] stdlib z42 (`Std.SoftHandle`)
