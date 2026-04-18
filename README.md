# z42

A **full-stack systems programming language** designed for productivity and performance.

- **z** — the last letter, standing for the final evolution
- **42** — the answer to the ultimate question

---

## Why z42?

| Problem | Solution |
|---------|----------|
| C# is bloated, Rust has ownership friction | Clean C# syntax, automatic GC, no lifetimes |
| Need systems code + scripts in one language | Bytecode runs directly (no VM overhead) or JIT/AOT compiles for speed |
| Embedding + native interop is painful | Zero-overhead `extern` FFI, C-compatible structs |
| Can't iterate on code without restart | Hot reload + eval() support (no restart needed) |
| One-size-fits-all language doesn't fit all | Per-project language customization (forbid features as needed) |

---

## Core Features

- **Execution modes:** Interpreter (fast startup), JIT (peak perf), AOT (stable latency) — mix per namespace
- **Bytecode-first:** Source → bytecode → execute/compile (not source → machine code)
- **Zero-overhead FFI:** `extern` methods call Rust impl directly (≤ 1 indirect jump)
- **Hot reload:** Update code without restarting (functions only, interpreter mode)
- **Multi-threaded:** GC-safe concurrency, structured async/await (L3)
- **Customizable:** Disable features per project (e.g., forbid nullable types, require exhaustive matches)
- **Type-safe:** Static typing + type inference; errors caught at compile time

---

## Quick Start

**Build the compiler and runtime:**

```bash
# Compiler (C# bootstrap)
dotnet build src/compiler/z42.slnx

# Runtime (Rust VM)
cargo build --manifest-path src/runtime/Cargo.toml
```

**Compile and run a program:**

```bash
# Compile to bytecode
dotnet run --project src/compiler/z42.Driver -- hello.z42 --emit zbc -o hello.zbc

# Run on the VM
cargo run --manifest-path src/runtime/Cargo.toml -- hello.zbc
```

See [CLAUDE.md](.claude/CLAUDE.md) for detailed build and workflow commands.

---

## Documentation

Start here based on what you want to know:

| I want to... | Read this |
|--------------|-----------|
| **Understand z42's design** | [`docs/design/philosophy.md`](docs/design/philosophy.md) |
| **See language syntax** | [`docs/design/language-overview.md`](docs/design/language-overview.md) |
| **Learn feature specs** | [`docs/features.md`](docs/features.md) |
| **Understand bytecode/IR** | [`docs/design/ir.md`](docs/design/ir.md) |
| **Understand execution modes** | [`docs/design/execution-model.md`](docs/design/execution-model.md) |
| **Learn native interop** | [`docs/design/interop.md`](docs/design/interop.md) |
| **Understand hot reload** | [`docs/design/hot-reload.md`](docs/design/hot-reload.md) |
| **See implementation progress** | [`docs/roadmap.md`](docs/roadmap.md) |

---

## Repository Layout

```
z42/
├── src/
│   ├── compiler/          # C# bootstrap compiler
│   ├── runtime/           # Rust VM (interp / JIT / AOT)
│   └── libraries/         # Standard library (.z42 source)
├── docs/design/           # Language design documents
├── examples/              # Example programs
└── .claude/               # Collaboration docs (CLAUDE.md, workflow rules)
```

---

## Implementation Status

| Phase | Focus | Status |
|-------|-------|--------|
| **L1** | Core language + pipeline | ✅ Complete |
| **L2** | Ecosystem, stdlib, VM quality | 🚧 In Progress |
| **L3** | Generics, async, ADTs, Traits | 📋 Planned |

See [docs/roadmap.md](docs/roadmap.md) for detailed milestones.

---

## License

TBD
