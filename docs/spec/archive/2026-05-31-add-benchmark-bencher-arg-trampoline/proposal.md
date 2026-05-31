# Proposal: `[Benchmark] void f(Bencher b)` via AST-level desugar

## Why

add-benchmark-runner-dispatch (#6) shipped `[Benchmark]` with a **zero-arg**
signature contract — the test author constructs the Bencher inside the body
(`var b = new Bencher(); b.iter(...); b.printSummary("name");`). The original
contract was `void f(Bencher b)` (runner constructs + injects the Bencher),
but that was dropped because the runner had no way to construct a Bencher
Value from Rust. Tracked as Deferred `bench-bencher-arg-trampoline`.

The Bencher-arg form is the ergonomic one users expect (xUnit / criterion
style):

```z42
[Benchmark]
void bench_add(Bencher b) {       // ← runner hands me a fresh Bencher
    b.iter(() => 1 + 2 + 3);      // ← I just measure; no boilerplate
}
```

vs. today's:

```z42
[Benchmark]
void bench_add() {
    var b = new Bencher();        // boilerplate
    b.iter(() => 1 + 2 + 3);
    b.printSummary("bench_add");  // boilerplate + manual label
}
```

This spec restores the Bencher-arg form **without** runner/runtime changes,
via a compiler AST-level desugar.

## What Changes

A new AST pass `BenchmarkDesugar` runs once, immediately before TypeCheck
in `PipelineCore.CheckAndGenerate` / `CheckOnly` (the single chokepoint
through which both single-file and package compilation flow). For each
**top-level** `FunctionDecl` carrying a `[Benchmark]` attribute **whose
sole parameter is of type `Bencher`**, it rewrites:

```z42
[Benchmark] void bench_add(Bencher b) { BODY }
```

into two functions:

```z42
// 1. demoted impl — original body, original Bencher param, attribute stripped
void bench_add$impl(Bencher b) { BODY }

// 2. synthesized zero-arg wrapper — carries the [Benchmark] attribute
[Benchmark] void bench_add() {
    var b = new Bencher();
    bench_add$impl(b);
    b.printSummary("bench_add");
}
```

Downstream, the synthesized zero-arg `bench_add()` flows through the
**existing** pipeline exactly like a hand-written zero-arg benchmark:
TypeCheck resolves `Bencher` / `printSummary` (the user's own `Bencher b`
param already proves they're in scope), the **unchanged** validator sees a
valid zero-arg `[Benchmark]`, IrGen emits a normal TIDX Benchmark entry, and
the runner dispatches it + captures `printSummary` stats into `bench_stats`
(#9 / #10). The demoted `bench_add$impl` has no test attributes → invisible
to test metadata.

**Key property: zero changes to the validator, runtime, or test-runner.**
The desugar produces only forms those layers already handle.

`$` is illegal in z42 identifiers (lexer: `[A-Za-z_][A-Za-z0-9_]*`), so the
`$impl` suffix is collision-proof against user code.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Codegen/BenchmarkDesugar.cs` | NEW | `public static CompilationUnit Run(CompilationUnit cu)` — pure transform. Detects top-level `[Benchmark]` fns with a single `Bencher` param; produces demoted impl + synthesized wrapper. No-op when no such fn exists (returns `cu` unchanged). |
| `src/compiler/z42.Pipeline/PipelineCore.cs` | MODIFY | Call `cu = BenchmarkDesugar.Run(cu);` at the top of `CheckAndGenerate` (before TypeChecker) and `CheckOnly` (before TypeChecker), so both the codegen and dump-bound paths see the desugared form. |
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | 只读引用 | No change — validator's zero-arg `[Benchmark]` rule (spec #6) already accepts the synthesized wrapper; the demoted impl has no attributes. |
| `src/compiler/z42.Tests/BenchmarkDesugarTests.cs` | NEW | Unit tests: (a) Bencher-arg → wrapper + $impl pair; (b) wrapper carries `[Benchmark]`, impl has none; (c) wrapper body shape (new Bencher / call impl / printSummary label = original name); (d) zero-arg `[Benchmark]` untouched; (e) non-Bencher param `[Benchmark]` untouched (validator still E0912s it); (f) `[Test]` untouched; (g) idempotence / no-op when no benchmarks. |
| `src/libraries/z42.test/tests/bench_demo.z42` | MODIFY | Add one `[Benchmark] void bench_add_argform(Bencher b) { b.iter(...); }` demonstrating the restored form runs + reports via the runner. |
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | MODIFY | Docstring on `ValidateBenchmarkFullSignature`: note the Bencher-arg form is now accepted *via desugar* (validator still only ever sees zero-arg post-desugar). |
| `docs/design/testing/testing.md` | MODIFY | (用法) Benchmark section: document both signatures (`void f()` and `void f(Bencher b)`), the desugar mechanism, label derivation; (设计思路) why AST-desugar over runtime-ObjNew / compiler-IR-synthesis. |
| `src/libraries/z42.test/src/Bencher.z42` | MODIFY | Docstring: both forms supported. |
| `src/libraries/z42.test/README.md` | MODIFY | Capability row: Bencher-arg form ✅. |

**只读引用：**
- `src/compiler/z42.Syntax/Parser/Ast.cs` — FunctionDecl / Param / TestAttribute / VarDeclStmt / NewExpr / CallExpr / MemberExpr / IdentExpr / LitStrExpr / ExprStmt / NamedType / Argument shapes
- `src/compiler/z42.Core/Text/Span.cs` — `default(Span)` for synthetic nodes
- `src/compiler/z42.Semantics/Codegen/IrGen.Tests.cs` — confirms `[Benchmark]` → TIDX Benchmark entry

## Out of Scope

- **Class-method benchmarks** (`[Benchmark]` inside a `class`) — v1 handles
  top-level functions only (matches all current benchmark usage; dogfood +
  demos are top-level). A class-method Bencher-arg benchmark stays
  un-desugared → validator E0912 (same as pre-spec). Documented limitation.
- **Runner reading Bencher fields directly** (bypassing printSummary) —
  desugar calls `printSummary("name")` so stats still flow through the #9
  stdout-parse path. Direct field read would need runtime ObjNew API; not
  worth it given #9/#10 already work.
- **Custom warmup/sample counts in the Bencher-arg form** — the synthesized
  `new Bencher()` uses defaults (10/100). Users needing custom counts use
  the zero-arg form with `new Bencher(W, S)`. A future `[Benchmark(warmup:,
  samples:)]` attribute could parameterize the synthesized ctor.
- **TIDX / zbc / zpkg format changes** — none; pure AST-level, pre-IrGen.

## Open Questions

- [x] **已裁决**：AST-desugar over (a) runtime-ObjNew API or (b) compiler
      IR-synthesis. AST-desugar is cleanest: synthesized code compiles
      through the normal pipeline (free name resolution / typecheck /
      codegen), needs zero validator/runtime/runner changes, no cross-package
      codegen-time resolution.
- [x] **已裁决**：synthesized wrapper keeps the **original name** (`bench_add`)
      so the runner/TIDX/JSON show the clean user-facing name; the body is
      moved to `bench_add$impl`. `$` collision-proof.
- [x] **已裁决**：printSummary label = original method name. Matches what a
      hand-written zero-arg benchmark would conventionally pass.
- [x] **已裁决**：trigger condition = exactly one param whose type short-name
      is `Bencher`. Zero-arg → untouched (validator passes). Other-arg →
      untouched (validator E0912). Avoids mis-firing on unrelated signatures.
- [x] **已裁决**：top-level only for v1; class-method Bencher-arg benchmarks
      remain a documented limitation (rare; same error as today).
