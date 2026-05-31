# Design: Benchmark Bencher-arg AST desugar

## Architecture

```
parse → CompilationUnit ──► BenchmarkDesugar.Run(cu) ──► TypeCheck → Validate → IrGen
                            │
                            │ for each top-level FunctionDecl fn where
                            │   fn.TestAttributes has "Benchmark"
                            │   AND fn.Params is [Param of type "Bencher"]:
                            │
                            ├─ demote: fn' = fn with {
                            │     Name = fn.Name + "$impl",
                            │     TestAttributes = null }
                            │
                            └─ synthesize wrapper:
                                 [Benchmark] void <origName>() {
                                     var b = new Bencher();
                                     <origName>$impl(b);
                                     b.printSummary("<origName>");
                                 }
                            cu' = cu with { Functions = rebuilt list }
```

Single seam (`PipelineCore.CheckAndGenerate` + `CheckOnly`). Everything
downstream is unchanged: the synthesized wrapper is indistinguishable from a
hand-written zero-arg benchmark.

## Decisions

### Decision 1: AST-desugar vs runtime-ObjNew vs compiler-IR-synthesis

**Options:**
- **A. Runtime ObjNew API** — expose Bencher construction + field-read to the
  test-runner (Rust). Runner constructs Bencher, calls `f(bencher)`, reads
  `MinNs`/… fields back. Couples test-runner to interp internals; replicates
  ObjNew ctor-chain logic outside the interp loop.
- **B. Compiler IR-synthesis** — IrGen directly emits the trampoline's IR
  (ObjNew + Call + VCall instructions). Requires resolving `Std.Test.Bencher`
  ctor + `printSummary` method at codegen time, cross-package — error-prone.
- **C. AST-level desugar** (chosen) — synthesize the trampoline as AST nodes
  before TypeCheck; the normal pipeline does all resolution/codegen.

**Decision: C.** Why:
- The synthesized `new Bencher()` / `printSummary(...)` resolve through the
  exact same name-resolution that already validated the user's `Bencher b`
  param — if their param type resolves, the synthesized references do too.
- Zero changes to validator / runtime / runner — the desugar emits only
  forms those layers already handle (verified: zero-arg `[Benchmark]` + a
  plain helper function).
- No cross-package codegen-time resolution (B's hazard); no runtime
  instantiation API + field-read replication (A's hazard).
- Pure `CompilationUnit → CompilationUnit` transform — trivially unit-testable
  by inspecting the rewritten function list, no VM needed.

### Decision 2: Naming — keep original name on the wrapper, `$impl` on the body

The synthesized **wrapper** takes the original name (`bench_add`); the
original **body** moves to `bench_add$impl`. So the runner/TIDX/JSON/pretty
all show the clean user-facing `bench_add`. `$` is illegal in z42 identifiers
(`[A-Za-z_][A-Za-z0-9_]*`), so `bench_add$impl` cannot collide with any user
symbol. Synthesized names are never re-lexed (they enter as `FunctionDecl.Name`
strings post-parse), so the `$` is inert downstream.

### Decision 3: Trigger predicate

Desugar fires iff a `FunctionDecl`:
1. has a `[Benchmark]` TestAttribute, AND
2. has exactly one `Param`, AND
3. that param's type is a `NamedType` with short name `"Bencher"`.

Rationale for each exclusion:
- **zero-arg `[Benchmark]`** → not touched; the validator passes it (spec #6).
- **other single-arg `[Benchmark] void f(int x)`** → not a Bencher → not
  touched → validator emits E0912 ("must take no parameters"), the correct
  error. Desugar must not mis-fire here.
- **multi-arg** → not touched → validator E0912.
- **`[Test]` / `[Setup]` / etc.** → no `[Benchmark]` → not touched.

Type match uses the param's `TypeExpr` short name (`NamedType.Name == "Bencher"`);
this is shape-only (pre-typecheck), mirroring `TestAttributeValidator`'s
existing `ExtractTypeName` short-name comparison.

### Decision 4: Validator stays strict zero-arg (no change)

Because the desugar runs **before** the validator and converts every
Bencher-arg `[Benchmark]` into a zero-arg one, the validator never sees the
Bencher-arg form. Its spec-#6 rule ("`[Benchmark]` must be `fn() -> void`")
remains correct and unchanged. This is the crux that keeps the blast radius
to one new file + two one-line pipeline insertions.

### Decision 5: printSummary label = original method name

The synthesized body calls `b.printSummary("<origName>")`. This makes the
`bench_stats.label` (#9) equal the method name — the natural identifier a
consumer correlates with. A hand-written zero-arg benchmark conventionally
passes its own name too, so behavior is consistent across both forms.

### Decision 6: Run in both CheckAndGenerate and CheckOnly

`CheckOnly` (the `--dump-bound` path) also runs the validator. To keep the
validator's view consistent (never see a Bencher-arg `[Benchmark]`), the
desugar runs at the top of both. It's a pure function, so running it twice
across paths is harmless; each compile invokes exactly one path.

## Implementation Notes

### Synthesized AST (all spans = `default(Span)`)

```csharp
// wrapper body statements:
// 1. var b = new Bencher();
new VarDeclStmt("b", null,
    new NewExpr(new NamedType("Bencher", S), new List<Argument>(), S), S)

// 2. <name>$impl(b);
new ExprStmt(
    new CallExpr(
        new IdentExpr(name + "$impl", S),
        new List<Argument> { new Argument(null, new IdentExpr("b", S), S) },
        S),
    S)

// 3. b.printSummary("<name>");
new ExprStmt(
    new CallExpr(
        new MemberExpr(new IdentExpr("b", S), "printSummary", S),
        new List<Argument> { new Argument(null, new LitStrExpr(name, S), S) },
        S),
    S)
```

Wrapper FunctionDecl:
```csharp
new FunctionDecl(
    Name: origName,
    Params: new List<Param>(),                 // zero-arg
    ReturnType: new VoidType(S),
    Body: new BlockStmt(stmts, S),
    Visibility: orig.Visibility,
    Modifiers: orig.Modifiers,                 // preserve static-ness etc.
    NativeIntrinsic: null,
    Span: S,
    TestAttributes: orig.TestAttributes)       // carries [Benchmark]
```

Demoted impl:
```csharp
orig with { Name = origName + "$impl", TestAttributes = null }
```

### Function-list rebuild

```csharp
var rebuilt = new List<FunctionDecl>(cu.Functions.Count + benchCount);
foreach (var fn in cu.Functions) {
    if (IsBencherArgBenchmark(fn)) {
        rebuilt.Add(Demote(fn));
        rebuilt.Add(SynthesizeWrapper(fn));
    } else {
        rebuilt.Add(fn);
    }
}
return cu with { Functions = rebuilt };
```

No-op fast path: if no function matches, return `cu` unchanged (avoids
allocating a new list for the common no-benchmark module).

### Modifiers / Visibility preservation

The wrapper inherits `orig.Visibility` + `orig.Modifiers` so a `static`
benchmark stays static (matters if benchmarks ever live in a static context).
The `$impl` keeps everything except the name + attributes.

## Testing Strategy

### Unit tests (`BenchmarkDesugarTests.cs`) — pure AST assertions

| Case | Input | Expect |
|------|-------|--------|
| bencher-arg desugars | `[Benchmark] void f(Bencher b){…}` | 2 fns: `f()` + `f$impl(Bencher)` |
| attribute migration | ^ | `f()` has `[Benchmark]`; `f$impl` has none |
| wrapper body shape | ^ | body = VarDecl(new Bencher) + Call(f$impl, b) + Call(b.printSummary, "f") |
| label = name | `[Benchmark] void bench_x(Bencher b)` | printSummary arg literal == "bench_x" |
| zero-arg untouched | `[Benchmark] void g(){…}` | unchanged, single fn |
| non-Bencher untouched | `[Benchmark] void h(int x){…}` | unchanged (validator will E0912 later) |
| `[Test]` untouched | `[Test] void t(){…}` | unchanged |
| no-benchmark no-op | module w/ only plain fns | returns same instance (or equal list) |
| impl preserves body | input body stmts | `f$impl` body == original body |

### Integration (`bench_demo.z42` + GREEN)

Add `[Benchmark] void bench_add_argform(Bencher b) { b.iter(() => 1+2+3); }`.
- Builds (desugar → typecheck → IrGen) cleanly
- `test-stdlib.sh z42.test` GREEN — the benchmark runs, dispatched by the
  runner as `bench_add_argform`, stats captured
- `cargo run -- … --format json` spot-check: `bench_stats.label ==
  "bench_add_argform"` present

### Validator regression

Existing `TestAttributeTests` Benchmark cases still pass:
- `Validate_BenchmarkZeroArg_PassesValidation` ✓ (desugar no-op on zero-arg)
- `Validate_BenchmarkWithBencherParam_ReportsE0912` — **this now changes**:
  with the desugar in the pipeline, a Bencher-arg benchmark is valid. BUT the
  validator unit test calls `TestAttributeValidator.Validate` directly
  (bypassing the pipeline desugar), so it still sees the raw Bencher-arg form
  → still E0912 at the validator layer. The test asserts validator behavior
  in isolation, which is unchanged. The end-to-end acceptance is covered by
  the new desugar tests + integration. **Update the test's doc comment** to
  note the validator is the post-desugar gate; pipeline-level acceptance is
  via BenchmarkDesugar. (Test assertion itself stays valid.)

## Risks

- **User has both `[Benchmark] f(Bencher)` and a real `f()`** → synthesized
  `f()` duplicates the user's → TypeChecker E0408 DuplicateDeclaration. Rare;
  natural error points at the conflict. Documented; not guarded in v1.
- **Class-method benchmarks** not handled (top-level only) — documented;
  same E0912 as today, no regression.
- **Span = default** on synthesized nodes → any diagnostic originating inside
  the synthesized wrapper reports at (0,0). Acceptable: the wrapper is
  trivial generated glue; real errors surface in the user's `$impl` body
  which keeps original spans.
