# z42

z42 is a statically-typed, garbage-collected programming language designed for
productivity and performance.

- **z** — the last letter, standing for the final evolution
- **42** — the answer to the ultimate question

## Design Goals

- **Syntax: C# as the baseline** — familiar, structured, and battle-tested
- **Garbage collected** — no manual memory management; focus on logic, not lifetimes
- **Strong static typing with inference** — catch errors at compile time, write less boilerplate
- **Multiple execution modes** — Interpreted, JIT, AOT — mixed freely at the namespace level via `[ExecMode]`
- **Ergonomic extensions** — ADT, exhaustive `match`, `Result<T,E>`, Traits — introduced in later phases without breaking the familiar baseline

## Language At a Glance

| Feature | Decision |
|---------|----------|
| Syntax baseline | C# 9–12 subset |
| Type system | Static typing with `var` inference |
| Null safety | `T?` nullable types, `??` coalescing, `?.` conditional |
| Error handling | `try`/`catch`/`throw`; `Result<T,E>` in L3 |
| Memory | Garbage collected — no ownership, no lifetimes |
| Execution modes | `Interp` / `JIT` / `AOT` — per namespace via `[ExecMode]` |
| Concurrency | `async`/`await` + `Task` — introduced in L3 |

See [`docs/features.md`](docs/features.md) for the full language design decisions.

## Repository Layout

```
z42/
├── src/
│   ├── compiler/     # Lexer, parser, type checker, IR codegen — C# (bootstrap)
│   └── runtime/      # Virtual machine (interpreter + JIT + AOT) — Rust
├── docs/features.md  # Language design decisions (what the language IS)
├── docs/roadmap.md   # Evolution phases and implementation milestones
├── docs/design/      # Implementation reference (syntax grammar, IR mappings)
└── examples/         # .z42 example source files
```

## Roadmap

See [`docs/roadmap.md`](docs/roadmap.md) for language evolution phases, implementation milestones, and feature progress.

## Getting Started

> Work in progress — see `docs/design/` for language design documents.

## License

TBD
