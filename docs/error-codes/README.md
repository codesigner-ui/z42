# Error code catalogs

z42 ships four diagnostic code spaces, organized by where the diagnostic
originates and where its catalog lives.

| Space | Origin | Catalog (source of truth) | Loaded by |
|---|---|---|---|
| `E####` | C# compiler (lex / parse / typecheck / irgen) | `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | C# directly |
| `W####` | C# compiler warnings | (in `DiagnosticCatalog.cs`) | C# directly |
| `WS###` | C# workspace / manifest | `src/compiler/z42.Project/WorkspaceCatalog.cs` | C# directly |
| `Z####` | **Rust VM runtime** | **[`Z.json`](Z.json)** ← cross-language SoT | Rust + C# (embedded resource) |

`E` / `W` / `WS` codes live entirely inside the C# compiler — only the catalog
class needs updating. `Z` codes are different: they're emitted from Rust but
need to be explainable from `z42c` too (so `z42c explain Z0905` works without
spawning the VM). The shared **`Z.json`** is consumed by both sides.

## Adding a new `Z####` code

1. Add an entry to [`Z.json`](Z.json) following the existing schema:
   ```json
   {
     "code": "Z0911",
     "title": "Short title (one line)",
     "description": "What it means, what causes it, how to fix.",
     "example": "// minimal repro / context"
   }
   ```
2. Emit it in Rust with `anyhow!("Z0911: ...")` or `bail!("Z0911: ...")`.
3. Run `cargo test -p z42_vm` — the `registry_audit` test will fail if any
   `Z[0-9]{4}` literal in `src/runtime/src/` doesn't have a catalog entry, or
   if the catalog has an entry that no code site uses.
4. C# picks it up automatically via the embedded `Z.json` resource — no C#
   change needed.

## Listing / explaining codes

```bash
z42c errors                    # E + W + WS + Z, grouped
z42c explain E0402             # compiler diagnostic
z42c explain WS003             # workspace diagnostic
z42c explain Z0905             # VM runtime diagnostic (via Z.json)

z42vm explain Z0905            # same content, served by Rust
z42vm errors                   # Z codes only
```

Both binaries render the same data because they read from the same `Z.json`.

## Why a JSON file and not generated code?

- **No build-time tooling coupling.** C# build doesn't need a Rust toolchain
  (and vice versa) to access the catalog.
- **Hand-editable.** No code-gen step to debug.
- **Cross-platform proof.** When future bindings (wasm / iOS / Android, see
  [docs/design/cross-platform-testing.md](../design/cross-platform-testing.md))
  need to surface VM errors, they read the same JSON via whatever loader makes
  sense for that platform.

The catalog is small and changes rarely (~5 codes today, ~10-15 expected in
L2-L3); a hand-maintained TOML/JSON pays off vs. a code-gen pipeline.
