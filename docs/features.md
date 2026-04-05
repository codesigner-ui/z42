# z42 Language Features

> **This document defines language design decisions** — what the language IS, not how it is implemented.
>
> **Update trigger:** Only update this file when a language design decision changes (rationale, phase assignment, or fundamental behavior). Implementation progress is tracked in [docs/roadmap.md](roadmap.md), not here.
>
> - Syntax grammar and code examples: [docs/design/language-overview.md](design/language-overview.md)
> - Evolution phases and milestones: [docs/roadmap.md](roadmap.md)

Each section states the design decision, rationale, and which phase it belongs to (L1 / L2 / L3).

---

## 1. Type System

**Decision:** Statically typed with local type inference.

- Primitive types: `int` (32-bit), `long` (64-bit), `float` (32-bit), `double` (64-bit), `bool`, `char` (32-bit Unicode), `string`, `void`
- Numeric aliases for C# compatibility: `sbyte`, `short`, `byte`, `ushort`, `uint`, `ulong`
- `var` infers the type of a local variable from its initializer
- User-defined types: `class`, `struct`, `record`, `enum`, `interface`

**Phase:** L1

---

## 2. Null Safety

**Decision:** Nullable types use `T?` syntax (C# style). `Option<T>` is not part of the language.

- Any type can be made nullable with a `?` suffix: `string?`, `int?`
- `T` is implicitly assignable to `T?`
- Null-coalescing operator `??` provides a fallback value
- Null-conditional operator `?.` short-circuits on null

**Rationale:** `T?` is familiar to C# developers and requires no new mental model. `Option<T>` adds expressive power for exhaustive null checking but increases complexity; it may be introduced as an opt-in in L3.

**Phase:** L1 (`T?`, `?.`, `??`) | L3 (possible `Option<T>` addition)

---

## 3. Memory Management

**Decision:** Garbage collected. No ownership, no borrowing, no lifetimes. Ever.

z42 always runs with a GC. The ownership model (Rust-style) is permanently out of scope.

**Rationale:** z42 prioritizes developer productivity and approachability. Ownership systems eliminate GC overhead but impose significant cognitive cost. z42 makes the opposite trade-off.

**Phase:** L1 (fundamental invariant, does not change across phases)

---

## 4. Error Handling

**Decision:** Exceptions in L1; `Result<T, E>` introduced as a complement in L3.

- **L1:** `try` / `catch` / `finally` / `throw`. Custom exception classes via inheritance.
- **L3:** `Result<T, E>` type with `?` propagation operator. Not a replacement — exceptions remain valid. The two styles coexist.

**Rationale:** Exceptions are familiar and sufficient for L1 semantics. `Result` enables explicit, zero-overhead error paths where control flow via exceptions is undesirable, and fits well with the functional patterns introduced in L3.

**Phase:** L1 (exceptions) | L3 (`Result<T, E>` + `?`)

---

## 5. Type Definitions

### Classes — reference types
`class` with fields, constructors, methods, auto-properties, and `static` members.
Single-class inheritance. Multiple interface implementation.

### Structs — value types
`struct` — stack-allocated, copied on assignment. Intended for small, self-contained data (e.g. `Color`, `Vector2`).

### Records — immutable data types
`record class` (reference semantics) and `record struct` (value semantics).
Auto-generated: structural equality, `ToString`, destructuring, and non-destructive update via `with`.

### Interfaces — behavioral contracts
`interface` defines a named set of method/property signatures. A type can implement multiple interfaces.
- **L1/L2:** runtime virtual dispatch (vtable)
- **L3:** `Trait` replaces `interface` as the primary abstraction for zero-overhead static dispatch. `interface` is retained for compatibility.

### Enums
Simple enums and enums with an explicit underlying integer type (`enum Foo : int`).

### Discriminated unions
- **L1:** simulated via `abstract record` hierarchy + `switch` pattern matching (no exhaustiveness enforced)
- **L3:** native algebraic data type (ADT) with exhaustive `match`

**Phase:** L1 (`class`, `struct`, `record`, `interface`, `enum`, abstract record pattern) | L3 (`Trait`, native ADT)

---

## 6. Functions

**Decision:** Top-level functions are supported without a class wrapper. Expression-body shorthand (`=>`). Default parameter values.

- Top-level and member functions
- Expression body: `int Double(int x) => x * 2;`
- Default parameter values (expanded at call site)
- `params` variadic parameters

**Phase:** L1

### Named Parameters (L3)
Call-site argument labeling: `Greet(name: "z42", prefix: "Hi")`.

**Phase:** L3

### Lambda & Closures (L3)
Anonymous functions assigned to `Func<>` / `Action<>` delegate types. Closures capture variables from the enclosing scope.

**Phase:** L3

---

## 7. Generics

**Decision:** Type parameters with `where` constraints. Monomorphized at compile time.

```z42
T Max<T>(T a, T b) where T : IComparable<T> { ... }
class Stack<T> { ... }
```

**Rationale:** Generics are required for type-safe collections and algorithms, but depend on a complete type inference and monomorphization pipeline. Deferred to L3 to keep L1 compiler scope manageable.

**L1 workaround:** `List<T>` and `Dictionary<K, V>` are provided via a pseudo-class strategy (hardcoded handling in the type checker and IR codegen) until generic infrastructure is ready.

**Phase:** L3

---

## 8. Collections

**Decision:** `T[]`, `List<T>`, `Dictionary<K, V>` are built-in with language-level syntax support.

| Type | Literal / Construction | Semantics |
|------|----------------------|-----------|
| `T[]` | `new T[n]` / `new[] { 1, 2, 3 }` | Fixed-size, contiguous |
| `List<T>` | `new List<T>()` | Dynamic array |
| `Dictionary<K, V>` | `new Dictionary<K, V>()` | Hash map |

- L1/L2: pseudo-class implementation inside the compiler (no stdlib dependency)
- L2 stdlib: `z42.collections` provides native implementations that replace pseudo-class

**Phase:** L1 (syntax + pseudo-class) | L2 (native stdlib `z42.collections`)

---

## 9. Pattern Matching

**Decision:** `switch` expression/statement in L1. Exhaustive `match` in L3.

- **L1 `switch` expression:** supports relational patterns (`> 0`), type patterns (`is Circle c`), and a default arm (`_`). Exhaustiveness is not enforced.
- **L3 `match` expression:** replaces `switch` for ADT values. All variants must be covered; the compiler rejects non-exhaustive matches.

**Rationale:** Non-exhaustive switch is sufficient for L1 where ADTs are simulated. True exhaustiveness checking requires native ADT support, which is an L3 concern.

**Phase:** L1 (`switch`) | L3 (`match`, exhaustive)

---

## 10. Concurrency

**Decision:** `async` / `await` with `Task` / `ValueTask`. No manual thread management.

- `async Task<T>` marks a function as asynchronous
- `await` suspends execution until the task completes
- `Task.WhenAll` / `Task.WhenAny` for structured parallel composition

No raw thread primitives are exposed. Synchronization via `lock` for shared state.

**Phase:** L3

---

## 11. Execution Model

**Decision:** Execution mode is declared per namespace via the `[ExecMode]` annotation. Three modes are supported: `Interp`, `Jit`, `Aot`.

```z42
[ExecMode(Mode.Interp)]   namespace Scripts.Config;    // always interpreted
[ExecMode(Mode.Jit)]      namespace Engine.Render;     // JIT compiled
[ExecMode(Mode.Aot)]      namespace Core.Crypto;       // AOT compiled
```

Cross-mode calls are transparent — the caller does not need to know how the callee is compiled. The default mode is determined by VM startup configuration.

**Phase:** L1 (annotation + `Interp` mode) | L1 (JIT, Cranelift) | L3 (AOT, LLVM)

---

## 12. Hot Reload

**Decision:** `[HotReload]` annotation on a namespace enables runtime function replacement without restarting the VM.

- Only valid when combined with `[ExecMode(Mode.Interp)]`
- After a reload, the next call to an updated function executes the new version
- Intended for game scripts, UI logic, or any scenario requiring rapid iteration

**Phase:** L1 (annotation spec) | L2 (full VM implementation)

---

## 13. Module System

**Decision:** File-scoped `namespace` declaration (C# 10+ style) with `using` imports. No module files separate from source files.

```z42
namespace Geometry;        // file-scoped, applies to entire file
using System.Math;
```

- Each file declares exactly one namespace
- `using` imports names from another namespace into scope
- Circular dependencies between namespaces are not allowed

Package distribution via `.zpkg` format is introduced in L2.

### Package Format: zbc and zpkg

Two artifact formats with distinct responsibilities:

| Format | Role | Self-contained | Use case |
|--------|------|---------------|----------|
| `.zbc` (fat) | Compilation unit | Yes — own string pool, type table | Single-file execution, incremental build cache |
| `.zpkg` (indexed) | Dev package | References `.cache/*.zbc` files | Development, incremental updates |
| `.zpkg` (packed) | Release package | Inlines optimized module entries | Distribution, patch downloads |

**Deterministic zbc:** A `.zbc` file is a pure function of its source content + compiler version. If the source hash is unchanged, recompilation produces an identical `.zbc`. This enables content-addressed incremental builds (analogous to Python's `.pyc`).

**Packed zpkg size optimization (L2):** When assembling a packed `.zpkg`, the build tool performs cross-module deduplication:
- All string literals from all modules are merged into a **shared string pool** in the zpkg header
- Type descriptors and namespace metadata are hoisted to a **shared type table**
- Each module entry becomes a "thin" bytecode (references shared pool indices instead of embedding its own)
- This reduces duplication significantly for packages with many files sharing common types and strings

The raw `.cache/*.zbc` files (fat) remain unchanged; the packing step produces optimized thin entries only inside the zpkg. Standalone `z42vm file.zbc` always uses fat zbc.

**Phase:** L1 (namespace/using) | L2 (package system, `.zpkg`, packed optimization)

---

## 14. Mutability

**Decision:** Variables are mutable by default in L1. Explicit immutability is introduced in L3.

- **L1:** `var x = 42;` — mutable local. All locals are mutable.
- **L3:** `let x = 42;` — immutable binding. `var` / `mut` for explicit mutability.

**Rationale:** Mutable-by-default matches C# semantics and reduces L1 cognitive overhead. Immutability-by-default is a meaningful ergonomic improvement but depends on the broader L3 type system (particularly Trait and ADT designs settling first).

**Phase:** L1 (mutable default) | L3 (`let` immutable, `mut` explicit)

---

## 15. String Model

**Decision:** `string` is an immutable reference type. `char` is a 32-bit Unicode code point. String interpolation and raw string literals are supported.

- Interpolation: `$"Hello, {name}!"` — expressions evaluated at runtime
- Raw strings: `"""..."""` (C# 11+ style) — no escape processing
- `string` is UTF-16 compatible internally; `char` is always a full Unicode scalar value

**Phase:** L1

---

## 16. Standard Library

**Decision:** The standard library is organized into named modules (`z42.core`, `z42.io`, `z42.collections`, `z42.text`, `z42.math`). `z42.core` is a default dependency — always loaded at VM startup without any `using` declaration.

### Design Philosophy: Best of Three Languages

Each module's API design draws from Rust, C#, and Python:

| Source | What we borrow |
|--------|----------------|
| **C#** | Naming conventions (`PascalCase`, `Console.WriteLine`, `List<T>`, `Dictionary<K,V>`), BCL structure, `IEquatable`/`IComparable` protocols, `StringBuilder`, LINQ-style collection methods (L3) |
| **Rust** | Trait-based abstractions (`IEquatable` → Trait in L3), `Result<T,E>` for error-returning APIs (L3), iterator chaining, zero-cost abstractions as the design goal |
| **Python** | "Batteries included" philosophy — common tasks should not require third-party packages; module names are readable and flat; `assert` is a first-class development tool |

The goal is familiar ergonomics for C# developers, with Rust's discipline on abstraction boundaries and Python's accessibility.

### `z42.core` — Default Dependency

`z42.core` is the **implicit prelude** of z42. It is:
- Loaded automatically at VM startup before any user code runs
- Available in every source file without a `using` declaration
- A compile-time error to explicitly shadow its names without qualification

This mirrors Python's `builtins` module and Rust's `std::prelude`. It provides:
`Object`, `Convert`, `Assert`, `IEquatable`, `IComparable`, `IDisposable`.

All other stdlib modules (`z42.io`, `z42.collections`, `z42.text`, `z42.math`) require an explicit `using` declaration and are loaded on demand.

**Phase:** L2 (`z42.core`, `z42.io`, `z42.math`) | L3 (`z42.collections` generic impl, `z42.text.Regex`)

---

## 17. Script-Friendly Execution

**Decision:** z42 supports lightweight, project-free execution modes in addition to full package-based deployment via `.zpkg`.

| Mode | Invocation | Phase |
|------|-----------|-------|
| Bytecode direct | `z42vm file.zbc` | L1 |
| Source file direct | `z42vm script.z42` | L3 |
| Inline eval | `z42vm -c "Console.WriteLine(42);"` | L3 |

- All modes are **project-free**: no `z42.toml`, no `.zpkg`, no manifest required
- `z42.core` is always auto-injected; other stdlib modules resolve on-demand via `using`
- `.zpkg` remains the standard format for application distribution and library packaging; these modes are for scripting and iteration

### Module Resolution: Two Search Paths

The VM maintains two independent search paths with fixed priority:

| Path | Format | Env var | Default dirs | Priority |
|------|--------|---------|--------------|---------|
| **module path** | `.zbc` only | `Z42_PATH` | `<cwd>/`, `<cwd>/modules/` | High |
| **libs path** | `.zpkg` only | `Z42_LIBS` | `<binary-dir>/../libs/`, `<cwd>/artifacts/z42/libs/` | Low |

`using Foo` resolution: scan module path first (read namespace from zbc header), then libs path (read `namespaces` field from zpkg manifest). Module path wins — local zbc can override a stdlib zpkg.

Conflict within the same tier (two files providing the same namespace) → compile error. Cross-tier override is valid and silent.

Namespace → package mapping uses exported metadata, not file names: `using z42.io` matches whichever zpkg has `"z42.io"` in its `namespaces` field, regardless of the zpkg's filename.

**Rationale:** Python-style accessibility — running a script or trying an idea should not require project setup. The project system (`z42.toml` + `.zpkg`) is for distribution; script modes are for iteration and embedding. Two separate paths make the format contract explicit: libs/ is for versioned packages, module path is for raw compiled units.

**Phase:** L1 (`.zbc` direct execution) | L2 (dual search path, namespace resolution, dependency recording) | L3 (source file direct, inline eval)

### zbc and zpkg File Roles

| File | Used In | Mode | Roles |
|------|---------|------|-------|
| `.zbc` (full) | `Z42_PATH`, direct execution | `flags=0x00` | Self-contained module: namespace header + full metadata + function bodies |
| `.zbc` (stripped) | zpkg `.cache/` only | `flags=0x01` | Compact cache: namespace header + body strings + function bodies; no SIGS/EXPT/IMPT |
| `.zpkg` (indexed) | `libs/`, `Z42_LIBS` | dev | References `.cache/*.zbc`; symbol index in package manifest |
| `.zpkg` (packed) | `libs/`, `Z42_LIBS` | release | Inlines all module bytecode; self-contained distributable |

**Invariant:** A stripped zbc (`.zbc` with `STRIPPED=1` flag) must **never** appear in `Z42_PATH`. Loading a stripped zbc directly is an error — it lacks the metadata needed for standalone execution. Stripped zbcs live exclusively in `.cache/` and are only loaded via zpkg index.

**namespace field:** Each zpkg manifest includes a top-level `namespaces: string[]` field listing all namespace names exported by the package. This allows the compiler and VM to resolve `using X` declarations by scanning package manifests without loading bytecode.
