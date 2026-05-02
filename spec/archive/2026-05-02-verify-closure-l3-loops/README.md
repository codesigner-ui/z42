# Verify: Closure R7 (loop-variable fresh binding) — auto-satisfied

> 状态：🟢 已完成 | 创建：2026-05-02 | 类型：test/docs（无新代码）

## Summary

Per archived `add-closures` Requirement R7, every loop iteration must
create a fresh binding for the loop variable so closures captured inside
the loop body each see their own iteration's value.

When this work was scheduled as `impl-closure-l3-loops` we expected to
write 150–200 lines to make the loop variable fresh each iteration. A
probe found the requirement is **automatically satisfied** by z42's
value-snapshot capture semantics (impl-closure-l3-core Decision 1):

- `MkClos` copies each captured value into the heap env at creation time.
- A closure built on iteration N gets an env carrying the value of `i`
  at iteration N — not a reference to `i`'s storage.
- Subsequent iterations rebinding `i` (in `for`) or rebuilding it (in
  `foreach`) leave previous closures' envs untouched.

C# 5's "晚绑定" bug existed because the C# compiler hoisted captured
locals to a display-class field shared across iterations. z42 has no
hoisting; the value-snapshot path naturally fixes both `foreach` AND
`for` (C# 5 only fixed `foreach`).

## Why test/docs?

No code paths needed change. This change adds:
- `src/runtime/tests/golden/run/closure_l3_loops/` — regression golden
  covering `foreach`, C-style `for`, `while`, and reference-type loop
  variable capture.
- `src/compiler/z42.Tests/ClosureCaptureTypeCheckTests.cs` — two
  TypeCheck unit tests confirming compile-time analysis works.
- `docs/design/closure.md` §4.3 — implementation note explaining the
  auto-satisfaction.
- `docs/roadmap.md` — L3-C2-loops marked ✅.

## Verification

- `dotnet build` / `cargo build` — green.
- `dotnet test` — 867/867 (+2 ClosureCaptureTypeCheck).
- `./scripts/test-vm.sh` — 213/213 (108 interp + 105 jit; new
  `closure_l3_loops` is interp-only mirroring other L3 closure goldens).

## Follow-ups

- C4 `impl-closure-l3-jit-complete` — JIT path for LoadFn / CallIndirect /
  MkClos (will let `closure_l3_loops` drop its `interp_only` marker).
- C5 / C6 — performance-tier optimizations (monomorphize / stack alloc).
