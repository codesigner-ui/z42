# Proposal: Populate `LineEntry.file` consistently in DBUG section

## Why

[surface-test-failure-source-location](../../archive/2026-05-30-surface-test-failure-source-location/)
shipped the test-runner side that surfaces `(file:line)` in failure
output — `failure_location` field for IDE jump-to-source. But the
e2e demo currently shows:

```
at Std.Assert.Equal(object,object) (line 1, col 1)
at Z42TestFailureLocationDemo.test_… (line 25, col 9)
```

Both frames fall back to the `(line N, col M)` shape without file path.
`first_user_frame` correctly skips these (no parseable `file:line` →
`primary_location: None`), so the headline runner feature — clickable
location — never lights up for >95% of tests.

Root cause is upstream in C# Codegen — `FunctionEmitter.Helpers.cs:49`:

```csharp
string? file = span.File != _sourceFile ? span.File : null;
```

The logic only records `file` when the span belongs to a *different*
file than the emitter's current source (cross-file references). For the
common case (function body entirely in one file), every `IrLineEntry`
gets `file=null`, which serialises as `u32::MAX` in DBUG and
deserialises as `LineEntry.file = None`. The Rust side has supported
both shapes since 2026-05-10 (`format_stack_trace` indents 144-164);
it's the producer that's been sparse.

This is the original design intent ("record cross-file references
only") but it predates the test-runner UX goal. Fix: always stamp
`_sourceFile`.

## What Changes

One line in `FunctionEmitter.Helpers.cs`:

```diff
- string? file = span.File != _sourceFile ? span.File : null;
+ string? file = span.File ?? _sourceFile;
```

Plus regenerate fixtures and stdlib zbc (DBUG content shifts — file
strings now appear in every entry; string pool grows by 1 entry per
unique source file per module).

No format / minor bump required — `LineEntry.file` field already exists
in DBUG v=X; this just populates it.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter.Helpers.cs` | MODIFY | 1 行：`TrackLine` 不再 short-circuit 同文件场景，always stamp `_sourceFile` 当 `span.File` 缺失 |
| `src/libraries/z42.test/tests/failure_location_demo.z42` | MODIFY | 加 1 个 assertion 断言 stack 包含 demo 文件路径（pre-spec 因 file=null 而无法断言，spec #3 留作 Deferred） |
| `src/tests/zbc-format/*/expected.json` + `*/source.zbc` | MODIFY | regen — DBUG 字节会变（更多 file str_idx 写入），string pool 内容也会扩 |
| `src/tests/zpkg-format/*/expected.json` + `*/source.zpkg` | MODIFY | 同上联动 |
| `src/libraries/*/src/*.zbc` (via regen-golden-tests) | MODIFY | stdlib zbc 全量 regen |
| `docs/design/testing/testing.md` | MODIFY | Deferred 段移除 `failloc-future-populate-file-strings` 条目（本 spec 已落地）+ "Failure location" usage 段更新 before/after 样本展示 real `(file:line)` |

**只读引用**：
- `src/runtime/src/exception/mod.rs:139-168 format_stack_trace`
- `src/runtime/src/metadata/zbc_reader.rs:569-571` LineEntry 反序列化
- `src/runtime/src/metadata/bytecode.rs:321` LineEntry struct shape

## Out of Scope

- **DBUG / TIDX format minor bump** — field 存在已久，不需要
- **Cross-file inlining tracking** — 函数 inline 后 `span.File` 已经是
  inline 源；此处只 fix "缺省退回 _sourceFile" 路径，不动 inline 语义
- **Compiler-side file path normalisation** (绝对/相对) — 保持现状；
  后续如需可独立 spec
- **`TestFailure.Location` 编译期注入** — 仍需 `[CallerLineNumber]` infra；
  独立 spec

## Open Questions

- [x] **已裁决**：保留 `span.File` 非空时优先 — 跨文件引用语义不变；仅在
      `span.File` 为 null（最常见情况）时退到 `_sourceFile`
- [x] **已裁决**：不引入新 sentinel — `_sourceFile` 在 emitter 构造时就已
      设置（来自 lexer 的源路径），始终非空；不会引入新 None 路径
