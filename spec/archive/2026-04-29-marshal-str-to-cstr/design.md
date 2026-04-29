# Design: Marshal `Value::Str` to `*const c_char` (C8)

## Architecture

```
exec_instr::CallNative {
    let mut arena = marshal::Arena::new();
    let z_args = args.iter().zip(method.params).map(|(reg, ty)|
        marshal::value_to_z42(frame.get(reg), ty, &mut arena)
    );
    unsafe { dispatch::call(cif, fn_ptr, &z_args, &params, &ret) }
    // arena drops here; CStrings released
}

marshal::Arena {
    // owns CString temporaries that back NATIVEPTR Z42Values
    cstrings: Vec<CString>,
}

(Value::Str(s), SigType::CStr) → {
    let cstr = CString::new(s.as_str())?;        // err on interior NUL
    let ptr  = cstr.as_ptr();
    arena.cstrings.push(cstr);                   // keeps ptr alive
    Z42Value{ tag: NATIVEPTR, payload: ptr as u64 }
}
```

## Decisions

### Decision 1: Arena owns CString backing

Each CallNative dispatch creates a fresh `Arena` on the stack. Marshal stores
each `CString` in `arena.cstrings`; raw pointers passed to libffi reference
the CString's internal buffer. When the dispatch returns and `arena` is
dropped, all CStrings are dropped and the pointers become invalid — but the
native call has already completed, so this is safe.

Alternatives considered:
- **Heap-leaked CString**: simpler API but leaks O(N) per call
- **thread_local Vec**: stateful, ordering issues with re-entrant calls
- **Rc<CString>**: pointer outlives call but adds RC overhead per arg

### Decision 2: Marshal API additions

```rust
// in marshal.rs
pub struct Arena {
    cstrings: Vec<std::ffi::CString>,
    // future: arrays, objects, etc.
}

impl Arena {
    pub fn new() -> Self { Self { cstrings: Vec::new() } }
}

pub fn value_to_z42(
    v: &Value,
    target: &SigType,
    arena: &mut Arena,
) -> Result<Z42Value> { ... }
```

Existing tests + caller sites that don't need temps pass `&mut Arena::new()`.

### Decision 3: New marshal cases

| Source | Target | Behaviour |
|--------|--------|-----------|
| `Value::Str(s)` | `SigType::CStr` | `CString::new(s)` → push to arena → ptr |
| `Value::Str(s)` | `SigType::Ptr` (unspecified element) | same as above (defensive — caller can request raw ptr to UTF-8) |
| (Other Str combinations) | numeric types etc. | Reject (existing fall-through) |

If `CString::new` fails (interior NUL): return `Err(anyhow!("Z0908: ..."))` — the same Z0908 family used for pinned-block constraint violations, since marshal failure is a similar "value can't cross FFI safely" error.

### Decision 4: Why `Value::Str` directly, not via PinnedView?

PinnedView returns the raw UTF-8 buffer pointer + length, **without** NUL terminator. For `*const c_char` (NUL-terminated C string) consumers, the caller must currently manually NUL-terminate by passing `(p.ptr, p.len)` to a length-aware function (e.g. `c_write(p.ptr, p.len)`).

This C8 path is for the **NUL-terminated** convention specifically — common for legacy libc APIs that don't take a length. Trade-off: extra alloc + copy per call, but matches the C ergonomic.

For length-aware bytes, `pinned` block remains the right tool (no extra copy).

## Implementation Notes

### CallNative arena lifetime

Place `let mut arena = marshal::Arena::new();` immediately before the
arg-marshal loop, and let it drop at scope end (after `dispatch::call`).
This keeps arena lifetime as short as possible.

### numz42-c PoC extension

```c
static int64_t counter_strlen(const char* s) {
    return (int64_t)strlen(s);
}

// new entry in COUNTER_METHODS:
{ "strlen", "(*const u8) -> i64", (void*)counter_strlen,
  Z42_METHOD_FLAG_STATIC, 0 },
```

Counter doesn't normally have a strlen method but it's a convenient host for
the test (avoids defining a new type for one method).

### z42 source extension (in fixture)

```z42
// add inside class NumZ42:
[Native(lib="numz42", type="Counter", entry="strlen")]
public static extern long Strlen(string s);

// possible additional Main, or separate test fn — TBD whether to extend
// existing fixture or add a new one (avoid coupling unrelated checks).
```

For test independence, prefer **adding a new fixture file** rather than
extending the Counter fixture. Or simply hand-craft IR in the e2e test
(less coupling, doesn't need z42c re-compile).

### Test approach: hand-crafted IR (preferred)

The e2e test can construct a Module manually with:
- `ConstStr`-build a Value::Str
- `CallNative numz42::Counter::strlen` passing the str register
- Return the result

This avoids extending the .z42 source fixture and recompiling. Hand-crafted
IR is idiomatic for the rest of the e2e suite.

## Testing Strategy

| Test | Verifies |
|------|----------|
| `marshal_str_to_cstr_ok` (unit) | (Value::Str, SigType::CStr) → NATIVEPTR with valid C-string ptr |
| `marshal_str_with_interior_nul_z0908` (unit) | CString::new fails → marshal returns Z0908 |
| `marshal_str_to_ptr_ok` (unit) | (Value::Str, SigType::Ptr) same path |
| `e2e_str_to_cstr_native_call` (integration) | Hand-craft IR: ConstStr "hello" → CallNative strlen → returns 5 |
| Existing tests don't regress | numz42_register / pin / e2e suite green |

## Risk

- **Risk 1**: Arena lifetime bugs (use-after-drop)
  - Mitigation: arena scope explicitly bounded to CallNative match arm; drop happens automatically
- **Risk 2**: Existing `marshal::value_to_z42` callers need updating
  - Mitigation: only one external caller (CallNative dispatch); test paths inject `&mut Arena::new()`

## Rollback

Single commit revert; no AST / IR / lexer changes.
