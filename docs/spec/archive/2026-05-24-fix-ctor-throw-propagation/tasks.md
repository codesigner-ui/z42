# Tasks: fix `throw` from constructor body being silently swallowed

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24
> 类型：fix (VM exception propagation)

**变更说明**：`throw new X(...)` inside a class constructor was being
silently dropped — the caller's `try { new C(badArg); } catch
(SomeException e) { ... }` couldn't catch it, and the program continued
as if no exception had occurred (the partially-constructed object was
even returned!).

**根因**：`interp::exec_object::obj_new` calls the ctor via
`super::exec_function(...)?;` and ignores the `ExecOutcome` result. The
`?` operator only handles the `Result` layer (anyhow internal errors);
user `throw` exceptions are wrapped in `Ok(ExecOutcome::Thrown(val))`
and were silently dropped. Compare with `Call` / `Builtin` paths in
`exec_instr.rs` which correctly propagate via
`if let Some(thrown) = ... { return Ok(Some(thrown)); }`.

**修复**：
1. Change `obj_new` to return `Result<Option<Value>>` (Some = thrown
   user exception, None = success), mirroring `exec_call::call`.
2. Match the `ExecOutcome` from `exec_function` and surface
   `ExecOutcome::Thrown` as `Ok(Some(val))`.
3. Update the `Instruction::ObjNew` arm in `exec_instr.rs` to
   propagate the thrown value via `return Ok(Some(thrown))`.

**文档影响**：none — this is a behaviour fix that brings ctor throws
in line with method throws as users naturally expect; no API change.
Mention in z42-test-runner / language overview not needed
(constructor throw catching wasn't documented as broken).

## Tasks

- [x] 1.1 MODIFY `src/runtime/src/interp/exec_object.rs` —
      `obj_new` returns `Result<Option<Value>>`, propagates Thrown
- [x] 1.2 MODIFY `src/runtime/src/interp/exec_instr.rs` —
      `Instruction::ObjNew` arm matches `Option<Value>` like `Call`
- [x] 2.1 NEW `src/libraries/z42.io/tests/ctor_throw_catch.z42` —
      regression test (constructor throws ArgumentException; catcher
      catches it)
- [x] 2.2 RE-ADD the dropped negative tests in:
      * `src/libraries/z42.io/tests/file_stream.z42`
        `test_unknown_mode_throws` was passing-after-fix already
        (re-verify)
      * Add similar `ProcessOutputStream` / `BufferedStream` tests
        that were skipped pending this fix
- [x] 3.1 `cargo check --release` + `cargo test --release --lib` (gc
      pre-existing only)
- [x] 3.2 `./scripts/test-stdlib.sh z42.io` — new test passes + the
      previously failing `test_unknown_mode_throws` now passes
- [x] 4.1 Archive + commit + push

## 备注

- Touches interpreter only; JIT path uses different lowering — see
  `jit/translate.rs` `ObjNew` handling. If JIT also has the same bug
  it's a separate spec (JIT isn't in the current GREEN gate per
  workflow rules: `interp 全绿前不碰 JIT`, and this fix is for
  interp).
