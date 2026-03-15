# z42 Language Overview

## Core Principles

1. **Safe by default** — memory safety, null safety, exhaustive pattern matching
2. **Fast by default** — zero-cost abstractions, AOT/JIT compilation
3. **Ergonomic** — minimal boilerplate, expressive syntax, great error messages
4. **Interoperable** — FFI with C, C#, and Rust ecosystems

---

## Syntax Sketch

```z42
// Module declaration
module hello

// Import
use std.io

// Function
fn greet(name: str) -> str {
    return "Hello, {name}!"
}

// Entry point
fn main() {
    let msg = greet("world")
    io.println(msg)
}
```

### Types

```z42
// Primitive types
let x: i32 = 42
let y: f64 = 3.14
let flag: bool = true
let ch: char = 'z'
let s: str = "hello"

// Inferred
let z = 42          // i32

// Option (no null)
let maybe: i32? = none
let value: i32? = 7

// Result
fn divide(a: f64, b: f64) -> f64! {
    if b == 0.0 { return error("division by zero") }
    return a / b
}
```

### Ownership & Memory

```z42
// Owned value — moved on assignment
let a = Buffer.new(1024)
let b = a               // a is moved, b owns the buffer

// Borrow (immutable)
fn inspect(buf: &Buffer) { ... }

// Borrow (mutable)
fn fill(buf: &mut Buffer) { ... }

// Region-based allocation (escape-hatch for performance)
region r {
    let tmp = r.alloc(MyStruct { ... })
    process(tmp)
}   // all region memory freed here
```

### Structs & Traits

```z42
struct Point {
    x: f64
    y: f64
}

trait Distance {
    fn dist(self, other: &Self) -> f64
}

impl Distance for Point {
    fn dist(self, other: &Point) -> f64 {
        let dx = self.x - other.x
        let dy = self.y - other.y
        return (dx*dx + dy*dy).sqrt()
    }
}
```

### Pattern Matching

```z42
enum Shape {
    Circle(radius: f64)
    Rect(w: f64, h: f64)
    Triangle(base: f64, height: f64)
}

fn area(s: Shape) -> f64 {
    match s {
        Circle(r)     => Math.PI * r * r
        Rect(w, h)    => w * h
        Triangle(b, h) => 0.5 * b * h
    }
}
```

### Async / Concurrency

```z42
async fn fetch(url: str) -> str! {
    let resp = await Http.get(url)?
    return await resp.text()
}

fn main() {
    let result = spawn fetch("https://example.com")
    // ...
    let text = await result
}
```

---

## Execution Mode Annotations

Modules can declare their preferred execution mode; the VM honours it:

```z42
#[exec = interp]   // always interpreted (scripting, hot-reload)
module scripts.config

#[exec = jit]      // JIT-compiled at runtime
module engine.render

#[exec = aot]      // ahead-of-time compiled to native
module core.crypto
```

Mixed execution is transparent — calling across mode boundaries is a normal function call.

---

## IR (Intermediate Representation)

z42 IR is a typed, SSA-form bytecode designed to target all three execution backends:

- Register-based (not stack-based) for JIT friendliness
- Explicit ownership / lifetime annotations preserved through IR
- Serialisable to a compact binary format (`.z42bc`)

Spec TBD in `specs/ir.md`.
