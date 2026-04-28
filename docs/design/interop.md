# Native Interoperability (FFI)

> This document describes how z42 code calls native (Rust/C) code and vice versa, including calling conventions, performance guarantees, and memory safety boundaries.

---

## Overview

z42 is designed to be **embedded in and callable from** native applications. The FFI (Foreign Function Interface) is not an afterthought — it's a core concern, with strict performance and safety rules.

**Three layers of interop:**

1. **z42 → Rust VM:** `extern` methods bind directly to Rust impl functions
2. **Rust VM ↔ Host App:** C ABI for embedding (load VM, call functions, pass data)
3. **Memory boundary:** Struct layout is C-compatible; GC pointers don't cross boundaries

---

## Layer 1: z42 → VM (InternalCall)

### Declaring Native Functions

z42 code calls native VM functions via `extern` + `[Native]`:

```z42
namespace Std.IO;

public static class Console {
    [Native("__println")]
    public static extern void WriteLine(string value);

    [Native("__readline")]
    public static extern string ReadLine();
}

namespace Std.Math;

public static class Math {
    [Native("__sqrt")]
    public static extern double Sqrt(double x);
    
    [Native("__abs")]
    public static extern double Abs(double x);
}
```

**Rules:**

- `extern` method **must** have `[Native("__name")]` attribute
- Missing `[Native]` → compile error `Z0903`
- Missing `extern` on `[Native]` method → compile error `Z0904`
- Function **cannot** have a body (use `;` not `{}`)
- `__name` **must** be registered in VM's `NativeTable` (see VM docs)
- Unregistered name → compile error `Z0901`
- Parameter count must match `NativeTable` definition; mismatch → `Z0902`

### Native Function Naming Convention

Native function names (the string passed to `[Native("...")]` and used as the
dispatch key in `corelib/mod.rs::dispatch_table`) follow a fixed format. New
builtins **must** comply; existing exceptions are legacy and not to be imitated.

#### Format

```
__<area>_<verb>[_<modifier>]
```

- **`__` prefix** — marks the symbol as VM-internal dispatch key; reserves the
  namespace from user-defined code (which never starts identifiers with `__`).
- **`<area>`** — domain prefix; required. ASCII lowercase, single word.
  Allowed values:

  | Area | Domain | Examples |
  |------|--------|----------|
  | `str` | string operations on `Std.String` | `__str_length`, `__str_char_at`, `__str_from_chars` |
  | `char` | `Std.char` operations | `__char_to_upper`, `__char_is_whitespace` |
  | `int` / `long` / `double` | primitive type ops (parse / hash / equals / to_string) | `__int_parse`, `__double_to_string` |
  | `math` | `Std.Math.Math` static methods | `__math_sqrt`, `__math_atan2` |
  | `obj` | universal object protocol | `__obj_get_type`, `__obj_ref_eq`, `__obj_hash_code` |
  | `file` | `Std.IO.File` static methods | `__file_read_text`, `__file_exists` |
  | `env` | environment / process | `__env_get`, `__env_args` |
  | `time` | clock / measurement | `__time_now_ms` |
  | `process` | host process control | `__process_exit` |

  When introducing a builtin in a **new domain**, add the area prefix to this
  table and pick a short, single-word identifier (no underscores inside the
  area).
- **`<verb>`** — snake_case action; required. Multi-word OK
  (`__str_starts_with`, `__file_read_text`).
- **`<modifier>`** (optional) — disambiguates overloads or units, e.g.
  `__time_now_ms` (units), `__math_log10` (variant). Avoid unless necessary.

#### Examples

```rust
// ✅ Conform — area prefix + verb
m.insert("__str_length",       string::builtin_length);
m.insert("__math_sqrt",        math::builtin_sqrt);
m.insert("__obj_get_type",     object::builtin_get_type);
m.insert("__file_read_text",   fs::builtin_file_read_text);

// ❌ Legacy bare verb — do NOT add new ones
m.insert("__println",   io::builtin_println);     // historical (since L1)
m.insert("__readline",  io::builtin_readline);
m.insert("__concat",    io::builtin_concat);
m.insert("__len",       io::builtin_len);
m.insert("__contains",  io::builtin_contains);
m.insert("__to_str",    convert::builtin_to_str);
```

#### Why the convention matters

- **stdlib maintainability** — anyone reading `__str_*` knows it backs string
  operations; bare `__concat` requires reading the implementation to know which
  type it operates on.
- **dispatch table grouping** — `corelib/mod.rs::dispatch_table` registers
  builtins in area-grouped sections; the naming convention keeps the file
  visually organized.
- **collision avoidance** — adding `__hash` would conflict across domains;
  `__str_hash_code` / `__int_hash_code` / `__char_hash_code` / `__obj_hash_code`
  coexist trivially.

#### Legacy bare names (do not add)

The following 9 bare-verb names predate the convention and are retained for
backward compatibility:

`__println`, `__print`, `__readline`, `__concat`, `__contains`, `__len`,
`__to_str`, `__time_now_ms`, `__process_exit`.

When migrating these is convenient (e.g. their implementing module reorganises),
prefer renaming to `__io_writeline` / `__io_readline` / `__io_concat` etc.
Otherwise they may stay as-is —— but **no new bare names**.

### Calling Convention

When z42 code calls a native function:

```z42
string result = Console.ReadLine();  // ← compiled to Builtin instruction
```

**Compilation:**

```
// IR:
r0 = builtin "__readline" []
// ↓
// zbc: Builtin(native_id=12, args=[])
```

**Runtime (Interpreter):**

1. Interpreter executes `Builtin` instruction
2. Looks up `native_id` in `NativeTable`
3. Calls the Rust function **directly** (no marshaling, no wrapper)
4. Returns result to r0

**Performance:** ≤ 1 virtual call dispatch overhead (typically negligible vs. the function's work).

### Native Function Signature Mapping

z42 types map directly to Rust types:

| z42 Type | Rust Type | Notes |
|----------|-----------|-------|
| `int` | `i32` | 32-bit signed |
| `long` | `i64` | 64-bit signed |
| `float` | `f32` | 32-bit float |
| `double` | `f64` | 64-bit float |
| `bool` | `bool` | Byte-sized, but used as bool |
| `char` | `u32` | Unicode code point (32-bit) |
| `string` | `GcHandle<String>` | Reference to GC'd string |
| `T[]` | `GcHandle<Array<T>>` | Reference to GC'd array |
| `List<T>` | `GcHandle<List<T>>` | Reference to GC'd list |
| Value type / `struct` | `<struct>` (by value) | C-compatible layout |
| Class instance | `GcHandle<Type>` | Reference to GC'd object |
| `T?` (nullable) | `Option<T>` | Rust enum: Some(t) or None |

**Example in Rust (`NativeTable`):**

```rust
// src/runtime/src/native_table.rs

pub fn println(s: GcHandle<String>) {
    let str_val = s.as_ref().unwrap().data.clone();
    println!("{}", str_val);
}

pub fn sqrt(x: f64) -> f64 {
    x.sqrt()
}

pub fn abs(x: f64) -> f64 {
    x.abs()
}

pub static NATIVE_TABLE: &[(&str, usize, NativeFn)] = &[
    ("__println", 1, native_impl::println as NativeFn),
    ("__sqrt", 1, native_impl::sqrt as NativeFn),
    ("__abs", 1, native_impl::abs as NativeFn),
    // ...
];
```

---

## Layer 2: Rust VM ↔ Host Application

### Embedding the z42 VM

Host applications (C, Rust, Python, Go) can instantiate and run z42 code via the VM's C ABI.

**Example (Rust embedder):**

```rust
use z42_vm::{VM, VMConfig};

fn main() {
    let mut vm = VM::new(VMConfig::default());
    
    // Load bytecode
    let bytecode = std::fs::read("script.zbc").unwrap();
    vm.load_module("script", bytecode).unwrap();
    
    // Call a function
    let result = vm.call("script::main", &[]).unwrap();
    println!("Result: {:?}", result);
}
```

**Example (C embedder):**

```c
#include "z42_vm.h"

int main() {
    Z42VM* vm = Z42_VM_Create();
    
    // Load bytecode
    Z42_VM_LoadModule(vm, "script", bytecode, bytecode_len);
    
    // Call function
    Z42Value result = Z42_VM_Call(vm, "script::main", NULL, 0);
    printf("Result: %ld\n", result.as_i64);
    
    Z42_VM_Destroy(vm);
    return 0;
}
```

### Data Exchange at the Boundary

#### Simple Values

Primitives (`int`, `float`, `bool`, `double`) pass through registers or C structs with no overhead.

#### Strings and Arrays

Strings and arrays are **GC pointers**. When crossing the boundary:

- **z42 → Rust:** Pass `GcHandle<String>`, Rust code reads it (no copy).
- **Rust → z42:** Rust allocates via VM heap allocator, returns `GcHandle<String>`.
- **Host ↔ VM:** Host must use VM's string API to create/read strings.

**Example (create a string in VM for z42 to read):**

```rust
let my_str = vm.create_string("Hello from Rust");
vm.call("process_string", &[my_str])?;
```

#### Structs

Structs with `[StructLayout(LayoutKind.Sequential)]` (or Rust `#[repr(C)]`) are passed by value.

```z42
// In z42:
public struct Point {
    public int X;
    public int Y;
}

[Native("__process_point")]
public static extern void ProcessPoint(Point p);
```

```rust
// In Rust (NativeTable):
#[repr(C)]
pub struct Point {
    pub x: i32,
    pub y: i32,
}

pub fn process_point(p: Point) {
    println!("Point: ({}, {})", p.x, p.y);
}
```

#### Objects

Class instances are always **GC references**. The host never directly accesses object memory:

```z42
public class Circle {
    public double Radius { get; set; }
}

[Native("__get_area")]
public static extern double GetArea(Circle c);
```

```rust
// In Rust:
pub fn get_area(c: GcHandle<Class>) -> f64 {
    let circle = c.as_ref().unwrap();
    let radius = circle.get_field("Radius").as_f64;
    std::f64::consts::PI * radius * radius
}
```

---

## Layer 3: Memory Safety Boundaries

### The Safety Contract

z42 enforces a **strict boundary** between managed and native code:

1. **Managed side (z42 code):** No null pointers, no buffer overruns, type-safe access.
2. **Native side (Rust VM impl):** Responsible for its own safety (Rust compiler enforces this).
3. **Boundary crossing:** The VM translates types and validates data passing in both directions.

**Invariant:** Native code cannot directly corrupt z42's heap.

### What Native Code CAN'T Do

- **Write to GC-managed objects directly.** Native code reads object fields via the VM's API (e.g., `get_field`, `set_field`).
- **Hold onto GcHandles across VM calls.** If a handle escapes the native function and is used after GC, it's undefined behavior (but such cases must be prevented at the language level).
- **Use raw memory pointers.** z42 has no unsafe pointers; native code must use the VM's object API.

### Struct Memory Layout

Structs are C-compatible:

```z42
public struct Color {
    public byte R;
    public byte G;
    public byte B;
    public byte A;
}
```

Memory layout (little-endian, no padding):

```
Offset  Field   Type
0       R       byte (1 byte)
1       G       byte (1 byte)
2       B       byte (1 byte)
3       A       byte (1 byte)
Total: 4 bytes
```

This struct can be passed to C code expecting `struct { uint8_t r, g, b, a }`.

---

## Performance Guarantees

### Zero-Overhead Abstractions

- **native → z42:** Calling a z42 function from native code has the same overhead as calling any VM function (function lookup + argument setup).
- **z42 → native (extern):** Direct native call; no marshaling, no wrapper trampolines. Overhead ≤ 1 indirect jump.
- **Struct passing:** By-value, no allocation, same as C.
- **Primitive arguments:** Register passing (x64 calling convention).

### What Has Overhead

- **GC pointers crossing boundaries:** Validation that the pointer is still valid (1 lookup in handle table).
- **Dynamic field access:** Native code reading object fields via `get_field` API (similar to reflection; use sparingly for hot paths).
- **String creation:** Allocating a new string in the VM heap (normal allocation cost).

### Best Practices for Performance

- Use `extern` functions for **hot paths** (physics, rendering, crypto).
- Use `extern` for **bulk operations** (prefer one large call over many small calls).
- Avoid calling z42 from native in tight loops; instead, call native from z42's hot loop.
- Pass structs by value when possible (cheaper than GC pointers).

---

## Example: Game Engine Integration

### Scenario

A game engine written in Rust needs to call z42 game logic.

**Rust side (host):**

```rust
// engine/src/main.rs
use z42_vm::VM;

fn main() {
    let mut vm = VM::new(VMConfig::default());
    let bytecode = std::fs::read("game_logic.zbc")?;
    vm.load_module("game", bytecode)?;
    
    let mut game_state = GameState { player_x: 0.0, player_y: 0.0 };
    
    // Frame loop
    for frame in 0..1000 {
        let dt = 0.016;  // 60 FPS
        
        // Call z42 script to update game logic
        vm.call("game::on_update", &[Value::from_f64(dt)])?;
        
        // Render
        render(&game_state);
    }
}
```

**z42 side (game script):**

```z42
// game_logic.z42
namespace Game;

[ExecMode(Mode.Interp)]  // Fast startup, hot reload support
[HotReload]              // Can reload without restarting Rust engine
public class GameLogic {
    public static float PlayerX;
    public static float PlayerY;
    
    [Native("__physics_step")]
    public static extern void PhysicsStep(float dt);
    
    public static void OnUpdate(float dt) {
        PhysicsStep(dt);  // Call Rust physics engine
        
        // Update game state (in z42)
        PlayerX += 0.5f * dt;
        PlayerY += 0.3f * dt;
    }
}
```

The **physics engine** (`PhysicsStep`) is written in Rust for performance; game logic is in z42 for fast iteration.

---

## Limitations & Constraints

### Currently Not Supported

- **Async across boundary:** z42 `async`/`await` (L3) cannot bridge to native async without explicit glue code.
- **Callbacks:** Native code cannot directly call z42 functions by pointer; must go through the VM's call API.
- **Generic FFI:** `extern` methods don't support generics (L1 doesn't have generics anyway).

### Future (L3+)

- **Typed FFI:** Generate C bindings from z42 extern declarations.
- **Async callback:** Native code can queue callbacks to be executed by z42.
- **Zero-copy iteration:** Iterate over z42 arrays in native code without copying.

---

## Related Documents

- [philosophy.md](philosophy.md) — Embedding-first design principle
- [language-overview.md](language-overview.md) — InternalCall syntax (`extern`, `[Native]`)
- [ir.md](ir.md) — Builtin instruction and native function dispatch
