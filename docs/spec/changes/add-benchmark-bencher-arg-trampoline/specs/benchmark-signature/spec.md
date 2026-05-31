# Spec: `[Benchmark]` Bencher-arg signature (via desugar)

## ADDED Requirements

### Requirement: Bencher-arg benchmark desugar

#### Scenario: single Bencher param desugars to wrapper + impl
- **WHEN** a top-level `[Benchmark] void f(Bencher b) { BODY }` is compiled
- **THEN** the desugar produces a zero-arg `[Benchmark] void f()` wrapper plus
  a plain `void f$impl(Bencher b) { BODY }` (attribute stripped)
- **AND** the wrapper body is `var b = new Bencher(); f$impl(b);
  b.printSummary("f");`

#### Scenario: wrapper is dispatched + reported by the runner
- **WHEN** the compiled module runs under z42-test-runner
- **THEN** a TIDX Benchmark entry exists for `f` (the wrapper), dispatched
  like any zero-arg benchmark
- **AND** `bench_stats.label == "f"` is captured (subprocess + in-process)
- **AND** `f$impl` produces no TIDX entry (it has no test attributes)

#### Scenario: zero-arg benchmark is untouched
- **WHEN** `[Benchmark] void g() { ... }` (no params) is compiled
- **THEN** the desugar leaves it unchanged (single function, still
  `[Benchmark]`), and the validator passes it (spec #6 rule)

#### Scenario: non-Bencher param benchmark is not desugared
- **WHEN** `[Benchmark] void h(int x) { ... }` is compiled
- **THEN** the desugar does NOT fire (param is not `Bencher`)
- **AND** the validator emits **E0912** ("must take no parameters") — the
  correct error for a malformed benchmark signature

#### Scenario: `[Test]` and other attributes untouched
- **WHEN** a `[Test]` / `[Setup]` / `[Teardown]` function is compiled (with or
  without params)
- **THEN** the desugar does not touch it (no `[Benchmark]` attribute)

#### Scenario: module with no benchmarks is unchanged
- **WHEN** a CompilationUnit has no Bencher-arg `[Benchmark]` function
- **THEN** `BenchmarkDesugar.Run` returns the function list semantically
  unchanged (no spurious synthesized functions)

#### Scenario: original body + param preserved in impl
- **WHEN** `[Benchmark] void f(Bencher b) { b.iter(() => work()); }` desugars
- **THEN** `f$impl`'s body is exactly the original body, and its parameter
  list is exactly `[Bencher b]`

## MODIFIED Requirements

### Requirement: `[Benchmark]` accepted signatures

**Before (spec #6):** `[Benchmark]` functions must be `fn() -> void` with
zero parameters; `void f(Bencher b)` is rejected with E0912.

**After:** Two author-facing forms are accepted:
1. `void f()` — zero-arg; author constructs the Bencher in the body
   (unchanged from #6)
2. `void f(Bencher b)` — Bencher-arg; the compiler desugars it (before
   TypeCheck) into form (1) plus a demoted `f$impl(Bencher b)` helper, so the
   runner constructs the Bencher and reports stats automatically

The validator itself is **unchanged** — it only ever sees form (1) because
the desugar runs first. Signatures that are neither (1) nor a single-`Bencher`
param (e.g. `void f(int x)`, `void f(Bencher b, int n)`) are not desugared and
still report E0912.

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [x] **AST desugar (new `BenchmarkDesugar` pass, pre-TypeCheck)**
- [x] **TypeChecker** (resolves synthesized `new Bencher()` / `printSummary` —
      no checker code change; uses existing resolution)
- [ ] IR Codegen (no change — synthesized wrapper is ordinary code)
- [ ] VM interp (no change)
- [ ] Test runner (no change — dispatches the zero-arg wrapper)

## IR Mapping

No new IR. The synthesized wrapper lowers to the same instructions a
hand-written zero-arg benchmark would: `ObjNew Bencher`, `Call f$impl`,
`VCall printSummary`, `Return`. The `[Benchmark]` flag flows to a TIDX
`TestEntryKind::Benchmark` entry exactly as in spec #6.
