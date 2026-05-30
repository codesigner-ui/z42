# Proposal: `[Benchmark]` runner dispatch (minimal v1)

## Why

z42.test ships a full `Std.Test.Bencher` class with warmup / sample loops
+ min/median/max/total stats + `printSummary` (`src/libraries/z42.test/
src/Bencher.z42`), and the compiler accepts `[Benchmark]` attribute
metadata — TIDX entry's `Kind=Benchmark` flag is emitted. **But the
test-runner discovery filter drops every non-`Test` entry**
(`src/toolchain/test-runner/src/discover.rs:49` and
`src/toolchain/test-runner/src/main.rs:180`), so a method tagged
`[Benchmark]` never executes. Users have to use `[Test]` with a manually
constructed `Bencher` as a workaround (documented in Bencher.z42:14-16
"runner mode is a future spec — for now use Bencher inside a regular
[Test]").

This means the headline framework feature ("write `[Benchmark]`, get
benchmarks") doesn't work. Zero adoption today (`grep [Benchmark]` across
stdlib / tests / examples returns zero hits — no migration cost).

Close the gap minimally: route `[Benchmark]` entries through the same
execution path as `[Test]`, with one signature constraint shift to match
what the runner can dispatch without object-construction infrastructure.

## What Changes

1. **Validator signature contract for `[Benchmark]` flips** from
   `void f(Bencher b)` (requires runner-side Bencher construction —
   needs a trampoline infrastructure we don't have yet) → **`void f()`**
   (user constructs `Bencher` inside the body, matching the existing
   workaround pattern).

   Migration cost: 0 — no stdlib / test / example uses `[Benchmark]`
   today. Public API change is upfront and clean.

2. **Discovery** includes both `TestEntryKind::Test` and
   `TestEntryKind::Benchmark` in the runner's iteration list.

3. **Execution** dispatches `[Benchmark]` identically to `[Test]` —
   same in-process invocation, same Outcome handling. The user's body
   owns timing + reporting via `Bencher.printSummary` (writes to stdout).

4. **Output labelling** distinguishes bench from test in pretty mode
   so users can grep `bench[…]` lines easily. TestResult gains an
   `is_benchmark: bool` field; pretty prints `✓ bench:<name>` instead
   of `✓ <name>`. TAP / JSON unchanged (orthogonal — they already
   carry status; new field surfaces via serde).

5. **Future spec** (`add-benchmark-bencher-arg-trampoline`): generate a
   compiler-side trampoline so `void f(Bencher b)` shape is supported
   again with the runner auto-constructing Bencher and reading stats.
   Out of this spec's scope; tracked as Deferred entry.

## Scope (允许改动的文件)

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | MODIFY | `ValidateBenchmarkFullSignature`: change "must take exactly one Bencher parameter" to "must take zero parameters" (mirrors `[Test]` shape). Diagnostic message + E0912 wording updated. |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY | E0912 catalog description updated to reflect new zero-arg requirement |
| `src/toolchain/test-runner/src/discover.rs` | MODIFY | Iteration filter `entry.kind != TestEntryKind::Test` → accept both Test and Benchmark; flag the result struct so downstream can label |
| `src/toolchain/test-runner/src/main.rs` | MODIFY | In-process discovery loop: same filter relaxation; `DiscoveredTestOwned` gets `is_benchmark: bool` field |
| `src/toolchain/test-runner/src/result.rs` | MODIFY | `TestResult` gets `#[serde(skip_serializing_if)]` `is_benchmark: bool` field (default false, omitted from JSON when false) |
| `src/toolchain/test-runner/src/format/pretty.rs` | MODIFY | Failed/Passed/Skipped branches prefix name with `bench:` when `is_benchmark` |
| `src/libraries/z42.test/src/Bencher.z42` | MODIFY | Remove "future spec" caveat in docstring; document new convention: `[Benchmark] void name()` with `var b = new Bencher(); b.iter(…); b.printSummary("name")` inside |
| `src/libraries/z42.test/tests/bench_demo.z42` | NEW | E2E demo: 1 `[Benchmark]` method + 1 `[Test]` method using Bencher inside (regression: pre-spec workaround still works) |
| `src/compiler/z42.Tests/TestAttributeTests.cs` | MODIFY | Update `[Benchmark]` signature validation tests: pre-spec `void f(Bencher b)` passed; now `void f()` passes and `void f(Bencher b)` fails with new E0912 |
| `docs/design/testing/testing.md` | MODIFY | (用法) § "Benchmark runner" — new section documenting `[Benchmark]` shape + how runner schedules + output format; (设计思路) explain why we chose zero-arg over Bencher-arg trampoline (infrastructure cost; backward path) |
| `src/libraries/z42.test/README.md` | MODIFY | Capability table — `Runner [Benchmark] 调度` row 状态 `📋 待开 spec` → `✅ add-benchmark-runner-dispatch`; description updated |

**只读引用：**

- `src/runtime/src/interp/exec_object.rs` — `ObjNew` (future trampoline spec will hook here)
- `src/runtime/src/metadata/test_index.rs:TestEntryKind` — Benchmark = 2 (no enum change needed)

## Out of Scope

- **Bencher-arg signature support (`void f(Bencher b)`)** — requires
  compiler-generated trampoline OR runner-side ObjNew API; bigger work.
  Tracked as `add-benchmark-bencher-arg-trampoline` future spec
- **JSON benchmark stats schema** (criterion-style baseline diff / raw
  samples in JSON output) — user's `printSummary` writes a human-line
  to stdout; structured JSON output is a future spec
- **Benchmark scheduling control** (warmup count override, sample count
  override via CLI) — Bencher constructor accepts both; CLI passthrough
  is a future spec
- **Per-benchmark timeout** — `[Timeout(milliseconds: N)]` already works
  on `[Benchmark]` via the validator's `hasTest || hasBenchmark` rule;
  spec just inherits this
- **Skip / Setup / Teardown for benchmarks** — Skip already works
  (compound w/ Benchmark validated); Setup/Teardown only run for Test
  kind per design — same constraint applies to Benchmark

## Open Questions

- [x] **已裁决**：zero-arg signature (`void f()`) over Bencher-arg form —
      avoids runner-side ObjNew infrastructure for v1; migration cost
      is zero (grep confirms no existing `[Benchmark]` users)
- [x] **已裁决**：not introducing new TestStatus variant for benchmarks —
      Passed/Failed/Skipped semantics still apply (a benchmark that
      crashes is Failed, like a test). `is_benchmark` flag is purely a
      label for output formatting
- [x] **已裁决**：no special "Benchmark wave" in test-stdlib.sh — the
      e2e demo file lives in `src/libraries/z42.test/tests/` alongside
      other dogfood; benchmark runs are just normal stdlib tests with a
      benchmark line in their output
