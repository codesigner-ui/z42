# Tasks: fix VM bailing on char-vs-char `<` / `>` comparisons

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：fix (VM comparison operands)

**变更说明**：`c < '0'` / `c >= '9'` and similar char-vs-char relational
comparisons in z42 source code bailed at the VM level with
`"type mismatch in comparison: Char vs Char"`. Every parser writing a
digit-classifier (or any range check) in pure z42 was broken.

**根因**：`interp::ops::numeric_lt` (and its JIT twin
`jit::helpers::numeric_lt_helper`) only matched `Value::I64` /
`Value::F64` operand combos. `Value::Char` fell through to the
`(a, b) => bail!(...)` arm. Char range checks have never worked at
the VM level — bug was latent until a stdlib consumer
(`YamlParser._LooksLikeInt`) exercised it post-yaml-landing.

**Symptom amplified by a second bug**: in `interp/mod.rs` the
exec-loop wraps internal errors with `e.context(format!("  at <fn> (line)"))`.
anyhow's `.context()` shifts the location string into the new topmost
position, and downstream `format!("{e}")` only renders the topmost —
so every "VM error" surfaced as `"  at <fn> (line)"` with NO clue
what blew up. Investigating yaml's mysterious failure required also
fixing this display loss.

**文档影响**：none beyond the spec archive — VM internal behavior fix,
fully transparent to z42 source code (the existing yaml parser starts
working again as-is).

## Tasks

- [x] 1.1 MODIFY `src/runtime/src/interp/ops.rs` — `numeric_lt` add 3
      Char cases (Char-Char, Char-I64, I64-Char). Char widens to i64
      via `c as u32 as i64`.
- [x] 1.2 MODIFY `src/runtime/src/jit/helpers/mod.rs` — `numeric_lt_helper`
      add same 3 Char cases.
- [x] 1.3 MODIFY `src/runtime/src/interp/mod.rs` — exec-loop error
      enrichment: build a new `anyhow!("{e}\n  at <fn> (loc)")` instead
      of `.context(...)` so `Display` shows the actual cause + location
      together (was: location replaced cause).
- [x] 2.1 NEW `src/libraries/z42.core/tests/char_compare.z42` —
      regression covering Char-Char + Char-Int + the exact
      `c < '0' || c > '9'` digit-classifier pattern that broke yaml.
- [x] 2.2 NEW `src/libraries/z42.yaml/tests/yaml_stream.z42` —
      previously dropped from `add-stream-overloads-to-format-parsers`
      pending this fix; now active (4 tests).
- [x] 3.1 `./scripts/test-stdlib.sh z42.core` — char_compare tests
      green (7 tests)
- [x] 3.2 `./scripts/test-stdlib.sh z42.yaml` — all 48 ParseBasic /
      ParseBlock tests previously failing now pass; pre-existing
      quoted-string bugs still fail (separate spec).
- [x] 4.1 Archive + commit + push

## 备注

- The 16 yaml quoted-string-handling tests still fail (e.g.
  `test_double_quoted_basic` returns `"hello"` instead of `hello`).
  These are real yaml-parser bugs unrelated to char comparison —
  separate fix-yaml-quoted-strings spec.
- Eq path (`==` / `!=`) was already char-aware via Value's `PartialEq`
  derive. Only the ORDER comparisons (lt/gt/le/ge) had the gap.
- JIT helper update is preserved in lockstep even though JIT isn't in
  the current GREEN gate — keeps the two implementations in sync for
  when JIT comes online.
