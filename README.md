# z42

z42 is a statically-typed, garbage-collected programming language designed for
productivity and performance.

- **z** — the last letter, standing for the final evolution
- **42** — the answer to the ultimate question

## Design Goals

- **Syntax: C# as the baseline** — familiar, structured, and battle-tested
- **Garbage collected** — no manual memory management; focus on logic, not lifetimes
- **Dynamic friendliness from Python** — single-file scripts, `eval`, interactive REPL, duck-typed APIs where it makes sense
- **Best-of-Rust ergonomics** — algebraic enums (ADT), exhaustive `match`, expressive generics with trait-style constraints
- **Strong static typing with inference** — catch errors at compile time, write less boilerplate
- **First-class concurrency** — `async`/`await`, structured concurrency, `lock`
- **Multiple execution modes** — Interpreted, JIT, AOT — mixed freely at the module/function level via `[ExecMode]`

## Language At a Glance

| Feature | Design Choice |
|---------|--------------|
| Syntax  | C# 9–12 subset (L1 baseline) |
| Types   | `int`, `long`, `double`, `string`, `bool`, `char`, user-defined classes/structs/records |
| Null safety | Nullable types `T?`, null-coalescing `??`, null-conditional `?.` |
| Error handling | `try`/`catch`/`throw` |
| Memory | Garbage collected — no ownership, no lifetimes |
| Execution | Interpreter / JIT / AOT — mixed freely at namespace level via `[ExecMode]` |

## Repository Layout

```
z42/
├── src/
│   ├── compiler/     # Lexer, parser, type checker, IR codegen — C# (bootstrap)
│   └── runtime/      # Virtual machine (interpreter + JIT + AOT) — Rust
├── docs/roadmap.md   # Language evolution phases and milestones
├── docs/design/      # Language specification documents
└── examples/         # .z42 example source files
```

## Roadmap

See [`docs/roadmap.md`](docs/roadmap.md) for language evolution phases, implementation milestones, and feature progress.

## Getting Started

> Work in progress — see `docs/design/` for language design documents.

## License

TBD
