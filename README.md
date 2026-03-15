# z42

z42 is a programming language that combines the best of C#, Rust, and Python.

- **z** — the last letter, standing for the final evolution
- **42** — the answer to the ultimate question

## Design Goals

- Strong static typing with type inference (C# / Rust influence)
- Memory safety without a garbage collector — ownership + regions (Rust influence)
- Expressive, readable syntax and a rich standard library (Python influence)
- First-class concurrency and async/await
- Multiple execution modes: **Interpreted**, **JIT**, **AOT** — mixed freely per module

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
| 3 | Rust VM — JIT mode (via Cranelift or LLVM) |
| 4 | Rust VM — AOT mode |
| 5 | Mixed execution: per-module mode selection |
| 6 | Self-hosting — rewrite compiler and tools in z42 |

## Getting Started

> Work in progress — see `specs/` for language design documents.

## License

TBD
