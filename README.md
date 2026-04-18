# z42

A **full-stack systems programming language** combining the ergonomics of C#, the discipline of Rust, and the accessibility of Python. Designed for **high performance, native interoperability, and flexible execution** — from embedded firmware to cloud backends.

- **z** — the last letter, standing for the final evolution
- **42** — the answer to the ultimate question

---

## What is z42?

z42 is:

- **All-in-one:** Single language for infrastructure, server, client, scripting, and embedded code — no polyglot.
- **Bytecode-native:** Compiles to efficient bytecode that runs **directly on the interpreter, with JIT/AOT as opt-in optimizations**. First startup is fast, memory footprint is lean.
- **Embedding-first:** Designed to be embedded in other applications with zero-overhead native interop via `extern` bindings and predictable memory layout.
- **Production-ready:** Strong type system, multiple execution modes (Interpreter / JIT / AOT), hot reload support, observable debugging hooks, comprehensive error reporting.
- **Customizable:** Language-level configuration (e.g., forbid nullable types, require exhaustive matches) enforced per project, enabling teams to adopt stricter safety rules.

**Why z42?**

| Problem | z42 Solution |
|---------|--------------|
| Tired of C#'s complexity and historical baggage? | Clean syntax, no unsafe pointers or magic, same productivity. |
| Need Rust's correctness without ownership complexity? | Strong static typing, type inference, GC. Unsafe only at boundaries. |
| Want Python's simplicity in production code? | Bytecode-based, supports eval() and REPL, hot updates without restart. |
| Building embedded systems and tools? | Native FFI, C-compatible struct layout, predictable performance, bytecode executes directly. |
| Managing heterogeneous workloads? | Mix execution modes per namespace — interpret hot-reload code, JIT game logic, AOT crypto algorithms. |

---

## Core Design Principles

See [`docs/design/philosophy.md`](docs/design/philosophy.md) for detailed design rationale.

### 1. **Three Execution Modes, One Language**

```
bytecode (.zbc)
      ↓
  ┌───┴───┬───────┐
  ↓       ↓       ↓
Interp   JIT     AOT
 (fast  (peak   (stable
 startup) perf)  latency)
```

- **Interpreter:** Direct bytecode execution; no compilation overhead, ideal for startup-sensitive and hot-reload workloads.
- **JIT:** Runtime compilation of hot code paths; competitive with C# / Java on numeric workloads.
- **AOT:** Offline compilation to native code (via LLVM); zero interpreter overhead, suitable for hard real-time systems.
- **Mixed:** Different namespaces run in different modes; cross-mode calls are transparent.

### 2. **Bytecode Is the IR**

z42 **never compiles directly to machine code**. Instead:

1. Source → Parser → Type-check → IR Codegen → **Bytecode (.zbc)**
2. Bytecode can be executed directly (Interp), or compiled to native code (JIT/AOT)

**Benefits:**
- Bytecode is interpreter-friendly: linear instruction stream, no register allocation.
- Bytecode is portable and distributable (like Java/.NET IL).
- JIT startup is faster than a static compiler (only hot code is compiled).

### 3. **C# Syntax with Better Defaults**

Familiar syntax for C# developers, but with thoughtful modifications:

- No unsafe pointers in user code (only in `extern` boundaries).
- Cleaner standard library naming (no `System.Collections.Generic` verbosity).
- Immutable bindings (`let`) introduced in L3 for explicit intent.
- Traits (Rust-style static dispatch) gradually replace vtables.

### 4. **Native Interoperability**

FFI is **not an afterthought**, it's a first-class design target:

```z42
[Native("__physics_step")]
extern void PhysicsStep(float dt);  // ← calls Rust impl, zero overhead

// Host app calls z42 code:
VM.Call("game::on_tick", dt);
```

- `extern` methods bind directly to VM-provided native functions.
- Struct fields have C-compatible memory layout.
- Performance guarantee: native call overhead ≤ 1 indirect jump.

### 5. **Dynamic Execution: eval() & Hot Reload**

z42 is not purely static. It supports:

- **eval():** Compile and execute code at runtime (for testing, configuration, REPL).
- **[HotReload]:** Update function code without restarting the VM. Ideal for game scripts, UI logic, rapid iteration.

### 6. **Performance & Efficiency**

- **Bytecode compression:** Bytecode is typically 40–60% smaller than source.
- **Memory-efficient GC:** Generational copying collection, efficient object layout.
- **CPU-friendly dispatch:** Interpreter loop optimized for cache efficiency.
- **No boxing overhead:** Value types are monomorphized in generics, not boxed.

---

## Language At a Glance

| Feature | Decision | Phase |
|---------|----------|-------|
| Syntax baseline | C# 9–12 subset | L1 |
| Type system | Static typing with `var` inference | L1 |
| Null safety | `T?` nullable; `Option<T>` later | L1 / L3 |
| Error handling | `try`/`catch`/`throw`; `Result<T,E>` in L3 | L1 / L3 |
| Memory | Garbage collected — no ownership, no lifetimes | L1–L3 |
| Execution modes | `Interp` / `JIT` / `AOT` — per namespace | L1 |
| Hot reload | `[HotReload]` annotation, `Interp` mode only | L2 |
| Syntax customization | Project-level language feature toggles | L3 |
| Concurrency | `async`/`await` + `Task` | L3 |
| Generics | Type parameters with `where` constraints | L3 |
| ADT & Traits | `enum`, `match`, static dispatch | L3 |

See [`docs/features.md`](docs/features.md) for full feature definitions and rationale.

---

## Repository Layout

```
z42/
├── src/
│   ├── compiler/      # Lexer, parser, type checker, IR codegen — C# (bootstrap)
│   ├── runtime/       # Virtual machine (interpreter + JIT + AOT) — Rust
│   └── libraries/     # Standard library source (.z42)
├── docs/
│   ├── design/
│   │   ├── philosophy.md      # Core design principles & positioning
│   │   ├── language-overview.md  # Syntax reference
│   │   ├── features.md           # Feature definitions & phase assignments
│   │   ├── ir.md                 # Bytecode instruction set
│   │   ├── interop.md            # Native FFI design
│   │   ├── execution-model.md    # Interpreter/JIT/AOT details
│   │   ├── hot-reload.md         # Hot update mechanism
│   │   └── customization.md      # Language customization framework
│   └── roadmap.md              # Evolution phases and milestones
├── examples/          # .z42 example source files
└── artifacts/         # Build outputs (ignored by git)
```

---

## Evolution Roadmap

z42 evolves in three anchored phases:

| Phase | Focus | Status |
|-------|-------|--------|
| **L1** | Minimal viable language: parse → type-check → bytecode → VM execution. | ✅ Complete |
| **L2** | Ecosystem maturity: packaging, stdlib, testing, VM optimizations. | 🚧 In Progress |
| **L3** | Advanced features: generics, lambdas, async, Traits, ADTs. | 📋 Planned |

See [`docs/roadmap.md`](docs/roadmap.md) for detailed milestones and implementation progress.

---

## Getting Started

**Build:**

```bash
# Compiler (C# bootstrap)
dotnet build src/compiler/z42.slnx

# Runtime (Rust VM)
cargo build --manifest-path src/runtime/Cargo.toml

# Run tests
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

**Hello World:**

```z42
// hello.z42
void Main() {
    Console.WriteLine("Hello, z42!");
}
```

```bash
dotnet run --project src/compiler/z42.Driver -- hello.z42 --emit zbc -o hello.zbc
cargo run --manifest-path src/runtime/Cargo.toml -- hello.zbc
# Output: Hello, z42!
```

See [`CLAUDE.md`](.claude/CLAUDE.md) for detailed build commands and workflows.

---

## Learn More

- **Language Design:** [`docs/design/philosophy.md`](docs/design/philosophy.md)
- **Syntax & Examples:** [`docs/design/language-overview.md`](docs/design/language-overview.md)
- **Features & Phases:** [`docs/features.md`](docs/features.md)
- **Implementation:** [`docs/design/ir.md`](docs/design/ir.md), [`docs/design/interop.md`](docs/design/interop.md)

---

## License

TBD
