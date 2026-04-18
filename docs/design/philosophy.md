# z42 Design Philosophy

> This document articulates the core design principles, target audience, and positioning of the z42 language. It serves as the north star for language evolution and feature decisions.

---

## Target Audience & Use Cases

z42 is designed for **systems programmers** who need:

1. **Full-stack capability** — From embedded firmware to cloud backends, without language switching
2. **High performance** — Native interop, predictable memory layout, efficient bytecode
3. **Development velocity** — Familiar C#-based syntax, strong type system, rapid iteration
4. **Production readiness** — Multiple execution modes (interpreter/JIT/AOT), observability, hot updates

**Not a target:** Pure scripting languages (use Python); systems-only languages (use Rust); corporate Java codebases (use C#).

---

## Core Design Principles

### 1. **C# Syntax with Better Defaults, Rust Discipline, Python Accessibility**

**Rationale:** C# is familiar and ergonomic; Rust enforces correctness at scale; Python makes simple things simple.

| Aspect | z42 Approach |
|--------|--------------|
| **Syntax base** | C# (naming, declarations, OOP structure) |
| **Null safety** | `T?` nullable (familiar), but encourage Option-like patterns in L3 |
| **Mutability** | Mutable by default in L1 (C# compatible); `let` immutable in L3 (Rust-like) |
| **Error handling** | Exceptions in L1; `Result<T,E>` + `?` operator in L3 (Rust-inspired) |
| **Trait system** | Interfaces as vtables (C#) → Traits as static dispatch (Rust, L3) |
| **Module system** | Flat namespaces (Python) + explicit imports (Rust); no deep hierarchies |
| **Stdlib naming** | `PascalCase` (C#), but short and readable (`math`, `io`, not `System.Linq.Collections`) |
| **Ownership** | None — always GC (Python/C#). No lifetimes, no borrow checker. |

**What we cut from C#:**
- Global `using` aliases (encourage clear names)
- Unsafe pointers (except in `extern` boundaries)
- LINQ (replaced by iterator chaining in L3)
- Complexity of covariance/contravariance (simplified for L1/L2)

---

### 2. **All-in-One: Interpreter, JIT, AOT — Bytecode-Native Design**

z42 **never compiles to machine code first**. The IR pipeline is:

```
Source → Parser → TypeCheck → IR Codegen → Bytecode (.zbc)
                                              ↓
                                    ┌─────────┼─────────┐
                                    ↓         ↓         ↓
                                  Interp     JIT       AOT
```

**Why bytecode-first?**

- **Interp mode:** Direct bytecode execution; no translation layer. First startup is fast, memory footprint is small.
- **JIT mode:** Bytecode → machine code at runtime; hot functions compiled on demand.
- **AOT mode:** Offline compilation to machine code (via LLVM); zero interpreter overhead for latency-critical paths.
- **Mixed mode:** Different namespaces can execute in different modes; transparent cross-mode calls.

The bytecode format must be **interpreter-friendly**: linear instruction stream, no register allocation, high-level operations (method calls, type checks, exception handling) map 1-to-1 to bytecode instructions.

**Performance invariants:**
- Bytecode size < source size (compression).
- Interpreter dispatch loop should be CPU cache-friendly.
- JIT tier-up (interp → JIT) happens invisibly; no user code change.

---

### 3. **Embedding-First & Native Interop**

z42 VMs are designed to be **embedded in other applications**, not just standalone executables.

**Boundaries with native code:**

- **Calling native:** `extern` methods bind to VM-provided `[Native("__name")]` functions. Zero overhead (direct call to Rust impl).
- **Calling from native:** VM exposes C ABI for hosting (similar to Python/Lua embeddings).
- **Data sharing:** Struct fields have predictable memory layout (C-compatible if annotated); no GC pointers across boundaries.
- **Performance guarantee:** Calling a native function from z42 costs ≤ 1 register indirect jump (no trampolines, no marshaling).

**Example use case:**
```z42
// In z42 game engine:
[Native("__physics_step")]
extern void PhysicsStep(float dt);  // ← calls Rust impl directly

// Host application calls z42 script:
VM.Call("game::on_tick", dt);       // ← z42 impl, can call back to native
```

---

### 4. **Language-Level Customization**

z42 supports **syntax-level configuration** — some constructs can be disabled per project, not just per developer.

**Examples (planned for L3):**

```toml
# In z42.toml:
[language]
allow_nullable_refs = false      # forbid `T?` in this project
allow_unsafe_extern = false      # forbid unsafe extern blocks
require_exhaustive_match = true  # require ADT match to be exhaustive
```

**Benefits:**
- Teams can enforce stricter rules (e.g., "no nullable refs except in I/O boundaries").
- Language can have "safe" and "unsafe" subsets; tools can require one or the other.
- Gradual migration: new code written in stricter mode, old code can be grandfathered in.

---

### 5. **Dynamic Execution: eval(), REPL, Hot Update**

z42 is not a pure static language. It supports dynamic execution modes:

#### **eval() — Compile & Execute at Runtime**

```z42
// Load and execute code on the fly
string code = "Console.WriteLine(42);";
VM.Eval(code);                  // parse → type-check → compile → execute
```

**Use cases:**
- Game scripting (load level scripts dynamically)
- Configuration evaluation
- Testing & tooling (verify behavior without recompilation)
- REPL / interactive shells

#### **Hot Reload — Update Functions Without Restart**

```z42
[HotReload]
namespace Game.Scripts;

void OnUpdate(float dt) {
    // ... updated code
}
```

When the script is reloaded:
- New code is parsed, type-checked, and compiled to bytecode.
- Existing instances of the function are **not** replaced in the call stack.
- The next call to `OnUpdate` sees the new version.
- No VM restart, no state loss (local variables, open files, etc.).

**Constraints:**
- Only in `[ExecMode(Interp)]` mode (bytecode must be available).
- Signature changes (param types, return type) cause a compile error; the old code remains live until the error is fixed.
- JIT/AOT code cannot hot-reload (recompilation overhead); use interp mode for hot-reload-heavy workloads.

---

### 6. **Garbage Collection: Memory Safety Without Compromise**

z42 is **always garbage collected**. There is no ownership model, no lifetimes, no borrowing.

**Why GC?**

- **Developer productivity:** Focus on logic, not memory management. No "borrow checker friction."
- **Safety by default:** No use-after-free, no double-free, no memory leaks (in z42 code).
- **Predictable:** GC is a known quantity; developers understand pauses, can optimize.
- **Familiar:** C#, Python, Java developers feel at home.

**GC Strategy:**

- **L1/L2:** Mark-and-sweep or generational collection (exact algorithm TBD).
- **L3:** Advanced features: write barriers for multi-threaded collection, concurrent marking.

**Not a limitation:** GC is the *right choice* for systems like game engines, servers, and embedded frameworks where developer velocity matters more than squeezing the last nanosecond.

---

### 7. **Multi-threading: Structured Concurrency**

z42 **supports multi-threading** with strong guarantees:

- **No data races:** Type system prevents shared mutable state without synchronization.
- **Structured concurrency:** `async`/`await` (L3) + `lock` for critical sections.
- **GC coordination:** GC is stop-the-world (L1/L2) or concurrent (L3); threads block briefly during collection.

**Concurrency Model (L3):**

```z42
[ExecMode(Mode.Jit)]
namespace Engine.Rendering;

async Task RenderFrame() {
    var data = await FetchSceneData();  // Asynchronous I/O
    await Task.WhenAll(
        RenderGeometry(data),
        RenderLights(data),
        RenderPostFx(data)
    );
}
```

**Not a goal (yet):** Lockless data structures, compare-and-swap primitives, or lock-free algorithms (can be added in L3+ if needed).

---

### 8. **Performance: CPU & Memory Efficiency**

z42 is not a "fast language" in the sense of C; it's a **well-optimized managed language with competitive performance for its class.**

**CPU optimization targets:**
- **Bytecode interpreter:** ≤ 5 cycles/instruction (cache-friendly dispatch).
- **JIT hot-path:** Competitive with C#/Java on numeric workloads.
- **Native calls:** Zero overhead (direct register indirect jump).
- **Method lookup:** vtable caching; inline caches for polymorphic sites (JIT only).

**Memory efficiency:**
- **Bytecode compression:** 40–60% smaller than source.
- **Object layout:** Compact, field ordering optimized, no padding waste.
- **No boxing overhead:** Value types monomorphized in generics.
- **GC efficiency:** Generational collection, low pause times (goal: < 10ms per collection).

**High performance characteristics:**
- **Numeric code:** JIT-compiled, competitive with C#/Java.
- **Object-oriented code:** Vtable dispatch, same cost as C++.
- **Server workloads:** Predictable latency with generational GC.
- **Game engines:** Mixed-mode execution (hot loops in JIT/AOT, scripts in interp).

**Not a goal:** Beat C/Rust on raw speed. Goal: **fast enough for production systems (game engines, servers, embedded), without unsafe.**

---

### 7. **Observable & Debuggable**

z42 supports:

- **Line number mapping:** Bytecode ↔ source line (`.zbc` includes line table).
- **Local variable names:** Debugger can inspect variable values.
- **Stack traces:** Full, readable call stack on exception.
- **Profiling hooks:** VM can expose function entry/exit, allocation, GC events to external profilers.
- **Bytecode inspection:** `disasm` tool reconstructs high-level operations from `.zbc`.

Not a goal in L1/L2: step-by-step debugging (deferred to L3 if there's demand).

---

## Comparison with Other Languages

| Aspect | z42 | C# | Rust | Go | Python |
|--------|-----|----|----|-----|--------|
| **Syntax** | C#-like | ✅ | Curly braces | Curly braces | Indentation-based |
| **Type safety** | Strong | ✅ | Strong (+ borrow) | Weak on interfaces | Dynamic |
| **Memory** | GC | ✅ | Ownership | GC | GC |
| **Compilation** | Bytecode-first | IL→JIT | LLVM | Custom | Bytecode/interp |
| **Native interop** | FFI-friendly | Via P/Invoke | Idiomatic | ✅ | ctypes |
| **Embedding** | ✅ | Limited | ✅ | As library | ✅ (C API) |
| **Hot update** | ✅ (interp mode) | Limited | No | No | ✅ |
| **Syntax customization** | ✅ (planned) | No | No | No | No |
| **Execution modes** | Interp/JIT/AOT | JIT only | Static | Static | Interp |
| **Target audience** | Systems + scripting | Enterprise | Systems | Backend | Scripting |

---

## Design Constraints

**Invariants (never change across phases):**

1. **Always garbage collected** — No ownership model, no lifetimes. Ever.
2. **Bytecode is the IR** — Compiles to bytecode first, never directly to machine code.
3. **Type safe by default** — Unsafe only at boundaries (`extern`); not a general escape hatch.
4. **Single inheritance** — Multiple interface implementation only; avoids diamond problem.
5. **No implicit global state** — All I/O and effects are explicit (via function calls or annotations).

**Phase-dependent decisions (may evolve):**

- L1: Exceptions only (no `Result<T,E>`); immutable-by-default not required (`var` is mutable); no generics.
- L2: Stable stdlib organization; packed `.zpkg` format; efficient JIT basics.
- L3: `Result<T,E>`, Traits, ADTs, Generics, Async/Await.

---

## Language Evolution

z42 evolves in **three anchored phases** (L1 → L2 → L3), each with a clear charter:

| Phase | Focus | Attitude to Features |
|-------|-------|----------------------|
| **L1** | Minimal viable language: parse → type-check → execute. | Only add if needed to reach L1 completeness. |
| **L2** | Ecosystem & quality: packaging, stdlib, testing, VM optimizations. | Defer features to L3; stabilize L1 first. |
| **L3** | Advanced features: generics, lambdas, async, Rust-inspired patterns. | Build on L1/L2 foundations; add only if architecturally clean. |

**Never add:** Complexity without a clear use case. Ambiguous grammar. Features that constrain future evolution.

---

## Key Principles Summary

1. **Static typing with type inference** — catch errors early, reduce boilerplate
2. **Always garbage collected** — no ownership, no lifetimes, maximum productivity
3. **Bytecode-first execution** — interpreter, JIT, and AOT from one bytecode
4. **Embedding-friendly** — zero-overhead native interop, C-compatible data layout
5. **Dynamic & flexible** — eval(), hot reload, customizable syntax per project
6. **High performance** — competitive with managed languages (C#, Java)
7. **Multi-threaded** — structured concurrency, no data races at the language level

---

## Related Documents

- [language-overview.md](language-overview.md) — Syntax examples and user guide
- [features.md](../features.md) — Feature definitions and phase assignments
- [ir.md](ir.md) — IR instruction set and bytecode format
- [interop.md](interop.md) — Native interoperability in detail
- [execution-model.md](execution-model.md) — Interpreter, JIT, AOT execution modes
- [hot-reload.md](hot-reload.md) — Hot update mechanism and constraints
- [customization.md](customization.md) — Language customization framework
