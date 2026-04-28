# Object Protocol — VM Implementation

This document describes how the VM implements the universal "object" protocol
methods (`ToString`, `Equals`, `GetHashCode`, `GetType`) — the methods every
type implicitly inherits from `Std.Object`.

The doc focuses on **runtime dispatch paths**, not language-level semantics.
For the user-facing semantics see [`language-overview.md`](language-overview.md).
For the C# emit side see [`compiler-architecture.md`](compiler-architecture.md).

---

## ToString

User code writes:

```z42
string s = obj.ToString();
string greeting = $"Hello, {name}!";   // implicit ToStr per interpolation hole
string concat   = "n=" + n;            // implicit ToStr on numeric operand
```

The IR uses one instruction:

```
ToStr dst, src    // emit `obj_to_string(src)` and store result in dst
```

`ToStr` is emitted at every site that needs a string from an arbitrary value —
string interpolation, `+` with a string operand, explicit `.ToString()` call
where the receiver is unknown at compile time, etc.

### Three dispatch paths (by `Value` variant)

```
                    ┌──────────────────────────────────┐
                    │   ToStr dst, src                  │
                    │   = exec_instr.rs::Instruction    │
                    └──────────────┬───────────────────┘
                                   │
                                   ▼
                    ┌──────────────────────────────────┐
                    │   dispatch.rs::obj_to_string     │
                    │   match Value variant            │
                    └─┬────────────┬─────────────────┬─┘
                      │            │                 │
        Value::Object │     Value::Str (special)     │  others (I64/F64/Bool/Char/...)
                      │            │                 │
                      ▼            ▼                 ▼
         ┌────────────────┐  ┌───────────────┐  ┌───────────────────┐
         │ vtable lookup  │  │ exec_instr.rs │  │ value_to_str       │
         │ "ToString"     │  │  VCall        │  │ (corelib::convert) │
         │ ↓ found?       │  │  hardcoded    │  │ Display-style fmt  │
         │ ├ yes: call    │  │  __str_to_   │  └───────────────────┘
         │ │   user fn    │  │   string     │
         │ └ no: fallback │  └───────────────┘
         │   __obj_to_str │
         │   builtin      │
         └────────────────┘
```

### Path 1 — `Value::Object`: vtable then builtin fallback

`dispatch.rs::obj_to_string` (entry point):

1. Take `type_desc` from the object's `Rc<RefCell<ScriptObject>>`.
2. Look up `"ToString"` in `type_desc.vtable_index` (O(1) HashMap).
3. **Hit** → resolve `vtable[slot].1` to a function name → look up in
   `module.func_index` → `exec_function(...)` → unwrap `ExecOutcome`:
   - `Returned(Some(Value::Str(s)))` → `s`
   - `Returned(Some(other))` → fall back to `value_to_str(&other)` (defensive)
   - `Returned(None)` → empty string (defensive; should never happen for
     a typed `string` return)
   - `Thrown(v)` → render as `<exception: {value_to_str(v)}>` (don't
     re-throw — `ToStr` is supposed to be infallible from the IR's view)
4. **Miss** → call `__obj_to_str` builtin (returns the simple class name like
   `Foo{...}`); used when a class doesn't override `ToString` (it inherits
   `Std.Object.ToString` which the stdlib script implements via this builtin).

### Path 2 — `Value::Str`: hardcoded shortcut

In `exec_instr.rs::Instruction::VCall` (line ~380), when the receiver is
`Value::Str`, method dispatch is hardcoded:

```rust
let builtin_name = match method.as_str() {
    "ToString"    => "__str_to_string",   // identity (returns self)
    "Equals"      => "__str_equals",
    "GetHashCode" => "__str_hash_code",
    other => /* snake_case → __str_<snake> stdlib builtin */
};
```

`Value::Str.ToString()` is **not** routed through `dispatch.rs::obj_to_string`
— instead it goes through the VCall hardcoded switch above.

> ⚠️ **Asymmetry note** (review2 §2.2): every other primitive (`Value::I64`,
> `Value::F64`, ...) routes through stdlib script via `primitive_class_name` →
> `Std.<type>.ToString`, but `Value::Str` retains a hardcoded VCall path. This
> is a known migration gap from the "primitive → stdlib script" effort. See
> P1-2 in the project review backlog. Until that is closed, `Value::Str` is
> the only primitive whose protocol methods cannot be overridden via stdlib
> script.

### Path 3 — Other primitives: `value_to_str` (basic Display)

For `Value::I64` / `Value::F64` / `Value::Bool` / `Value::Char` / `Value::Null`
/ `Value::Array(...)` etc., `obj_to_string` falls through to
`corelib::convert::value_to_str`, which is a simple `Display`-style formatter:

| Value | Output |
|-------|--------|
| `I64(n)` | `n.to_string()` |
| `F64(f)` | `f.to_string()` |
| `Bool(b)` | `"true"` / `"false"` |
| `Char(c)` | `c.to_string()` |
| `Null` | `"null"` |
| `Array(rc)` | `[elem0, elem1, ...]` (recursive) |
| `Object(rc)` (defensive — shouldn't reach here) | `ClassName{...}` |

Note: this path is the **fallback** for `ToStr` IR instruction. When user code
writes `n.ToString()` on a typed `int`, the call goes through the **interp
VCall** with receiver type → stdlib script `Std.int.ToString` (not directly
through `value_to_str`). `value_to_str` is only the implicit format for
`ToStr` instructions (string interpolation, `+`).

---

## Equals / GetHashCode / GetType

These follow analogous paths but currently lack a centralised dispatch helper
(unlike `obj_to_string`):

- **`Equals` / `GetHashCode`**: routed through `VCall`. For `Value::Object` →
  vtable lookup. For primitives → hardcoded builtins (`__int_equals`,
  `__double_hash_code`, `__char_equals`, ...). For `Value::Str` → same
  hardcoded shortcut as `ToString` above.
- **`GetType`**: always routes through `__obj_get_type` builtin (returns a
  `Std.Type` object holding name + qualified-name).

**TODO**: consolidating these into a single `dispatch.rs::obj_method` helper
(parameterised by method name) is a candidate refactor — same pattern as
`obj_to_string`. Tracked for future cleanup.

---

## Call sites

| IR instruction | Where emitted | Calls |
|----------------|---------------|-------|
| `ToStr dst, src` | string interpolation `$"..."` per hole; `+` with string operand on the other side | `obj_to_string` |
| `VCall dst, recv, method, args` | every `obj.method(...)` user call | path-by-receiver dispatch (above) |

---

## Why three paths, not one

History:

1. **`Value::Object` vtable** (Path 1) is the canonical OOP dispatch — once
   `Std.Object.ToString` was rewritten as a script that delegates to
   `__obj_to_str`, the path naturally emerged from "look up vtable; if missing,
   invoke fallback builtin".

2. **`Value::Str` hardcoded** (Path 2) predates the script-first migration.
   Strings used to have *all* methods as VM intrinsics. Migration moved most
   of them to scripts (see [`stdlib.md`](stdlib.md)) but `ToString` /
   `Equals` / `GetHashCode` were not yet migrated — and fixing this requires
   `Std.String` to declare these methods as scripts that delegate to the
   underlying builtin (a small but currently uncompleted PR).

3. **`value_to_str` fallback** (Path 3) covers `ToStr` IR instructions on
   primitives where the receiver type is statically `Value::I64` etc. and the
   compiler emits implicit conversion. Once Path 2's asymmetry is fixed, this
   path remains valid as the "infallible Display" used by interpolation.

---

## Related

- [`stdlib.md`](stdlib.md) — script-first stdlib structure
- [`compiler-architecture.md`](compiler-architecture.md) — IR `ToStr` / `VCall` emission
- [`vm-architecture.md`](vm-architecture.md) — `Value` enum + interpreter loop
- review2 §5.4 (this doc's origin) + §2.2 (Path 2 asymmetry tracking)
