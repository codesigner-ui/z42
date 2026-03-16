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
| Syntax  | C# 9–12 subset as Phase 1 baseline |
| Types   | `int`, `long`, `double`, `string`, `bool`, `char`, user-defined classes/structs/records |
| Enums   | Algebraic data types (Rust-style) with exhaustive `match` |
| Generics | Type parameters with trait-style `where` constraints |
| Error handling | `try`/`catch`/`throw` today; `Result<T,E>` + `?` operator in Phase 2 |
| Null safety | Nullable types `T?`, null-coalescing `??`, null-conditional `?.` |
| Scripting | Single-file execution without a project file; built-in `eval` |
| Memory | Garbage collected — no ownership, no lifetimes |

## Repository Layout

```
z42/
├── src/
│   ├── compiler/     # Lexer, parser, type checker, IR codegen — C# (bootstrap)
│   ├── runtime/      # Virtual machine (interpreter + JIT + AOT) — Rust
│   ├── stdlib/       # Standard library — z42 (once self-hosting)
│   └── tools/        # CLI driver, REPL, package manager — C#
├── specs/            # Language specification documents
├── docs/             # User-facing documentation
└── tests/            # Test suite
```

## Implementation Phases

| Phase | Goal |
|-------|------|
| 0 | Language spec, IR design, project skeleton |
| 1 | Bootstrap compiler in C# (lexer → parser → type checker → IR) |
| 2 | Rust VM — interpreter mode |
| 3 | Rust VM — JIT mode (via Cranelift) |
| 4 | Rust VM — AOT mode (via LLVM) |
| 5 | Mixed execution: per-module/function mode selection |
| 6 | Self-hosting — rewrite compiler and tools in z42 |

## Getting Started

> Work in progress — see `specs/` for language design documents.

## License

TBD
