# z42

A **full-stack systems programming language** designed for productivity and performance.

- **z** — the last letter, standing for the final evolution
- **42** — the answer to the ultimate question

> 🚧 **z42 is under active, relentless iteration.** The language, compiler, VM, and toolchain are evolving rapidly and not yet stable for production use. What's landing here is being built with uncompromising taste — expect the final result to be **genuinely stunning**. Star the repo and watch it unfold.

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

### 1. Get z42

z42's build/test/dev tooling is **itself written in z42** (the `xtask` CLI), so
you bootstrap by first downloading a prebuilt launcher — the one native primer
(chicken-and-egg: you need a working z42 to run the z42-implemented tooling):

```bash
git clone https://github.com/codesigner-ui/z42 && cd z42
./scripts/install-z42.sh                       # → ./.z42/  (z42 launcher + z42c + z42vm + stdlib)
                                               #   Windows: scripts\install-z42.bat
export PATH="$PWD/.z42:$PWD/.z42/bin:$PATH"     # put z42 / z42c / z42vm on PATH
```

> `install-z42.sh` downloads the prebuilt package (version from
> `versions.toml [toolchain.z42].launcher`, default `nightly`) into a
> project-local, gitignored `./.z42/` — it never touches your system.

### 2. Build the xtask CLI, then drive everything through it

```bash
# build the dev CLI against the downloaded stdlib (Z42_LIBS points z42c at .z42/libs):
Z42_LIBS="$PWD/.z42/libs" z42c build scripts/xtask.z42.toml --release   # → artifacts/xtask/xtask.zpkg

z42 xtask.zpkg build all     # compiler + runtime + stdlib (from source)
z42 xtask.zpkg test          # full gate (compiler + vm + cross-zpkg + stdlib)
z42 xtask.zpkg --help        # all commands (build / test / deps / regen / bench / package)
```

### 3. Compile + run a z42 program

```bash
z42c build path/to/app.z42.toml --release   # → <out_dir>/<name>.zpkg  (see examples/*.z42.toml)
z42 <out_dir>/<name>.zpkg                    # run it via the launcher
```

A green `z42 xtask.zpkg test` already proves the toolchain compiles and runs z42
end-to-end. See [examples/](examples/) for project layouts.

> **Prerequisites:** git + .NET 10 SDK + Rust stable (`dotnet --version && rustc --version`).
> **Building the whole toolchain from source** (no prebuilt download) and the full
> bootstrap details live in [docs/workflow/building/](docs/workflow/building/).
> Full build / test / packaging / CI / release workflows: [docs/workflow/](docs/workflow/).
Collaboration workflow: [.claude/CLAUDE.md](.claude/CLAUDE.md).

---

## Documentation

Start here based on what you want to know:

| I want to... | Read this |
|--------------|-----------|
| **Understand z42's design** | [`docs/design/philosophy.md`](docs/design/philosophy.md) |
| **See language syntax** | [`docs/design/language/language-overview.md`](docs/design/language/language-overview.md) |
| **Learn feature specs** | [`docs/features.md`](docs/features.md) |
| **Understand bytecode/IR** | [`docs/design/runtime/ir.md`](docs/design/runtime/ir.md) |
| **Understand execution modes** | [`docs/design/runtime/execution-model.md`](docs/design/runtime/execution-model.md) |
| **Learn native interop** | [`docs/design/language/interop.md`](docs/design/language/interop.md) |
| **Understand hot reload** | [`docs/design/runtime/hot-reload.md`](docs/design/runtime/hot-reload.md) |
| **See implementation progress** | [`docs/roadmap.md`](docs/roadmap.md) |

---

## Repository Layout

```
z42/
├── src/
│   ├── compiler/          # C# bootstrap compiler
│   ├── runtime/           # Rust VM (interp / JIT / AOT)
│   ├── libraries/         # Standard library (.z42 source)
│   └── toolchain/         # Companion toolchain (host / debugger / packager / workload)
├── scripts/               # xtask dev CLI (build / test / package) + install primers
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

z42 is released under the [MIT License](LICENSE).
