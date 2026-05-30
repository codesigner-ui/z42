# Tasks: Populate LineEntry.file consistently

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：fix (compiler IR emit)

## 进度概览

- [x] 阶段 1: 1-line fix in FunctionEmitter
- [x] 阶段 2: Strengthen failure_location_demo assertion
- [x] 阶段 3: Regen zbc/zpkg fixtures + stdlib zbc
- [x] 阶段 4: Docs: remove Deferred entry + update Failure-location samples
- [x] 阶段 5: GREEN + commit + archive

## 阶段 1

- [x] 1.1 `src/compiler/z42.Semantics/Codegen/FunctionEmitter.Helpers.cs:49`
  `string? file = span.File != _sourceFile ? span.File : null;`
  → `string? file = span.File ?? _sourceFile;`

## 阶段 2

- [x] 2.1 `src/libraries/z42.test/tests/failure_location_demo.z42`
  - 加 `if (!e.StackTrace.Contains("failure_location_demo")) { Assert.Fail(...); }`
  - 删除 spec #3 内联注释里"file path is missing today"段（已修）

## 阶段 3

- [x] 3.1 `./src/tests/zbc-format/generate-fixtures.sh` regen
- [x] 3.2 `./src/tests/zpkg-format/generate-fixtures.sh` regen
- [x] 3.3 `./scripts/regen-golden-tests.sh --release` regen stdlib + tests
- [x] 3.4 `dotnet test --filter "FullyQualifiedName~Zbc|FullyQualifiedName~Zpkg"` GREEN

## 阶段 4

- [x] 4.1 `docs/design/testing/testing.md` Deferred 表删 `failloc-future-populate-file-strings` 行
- [x] 4.2 同节 Failure-location usage 段：把 before/after sample 的 `(line N, col M)` fallback 换成 `(file:line)` 真形态

## 阶段 5

- [x] 5.1 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 5.2 commit + push
- [x] 5.3 归档 `docs/spec/changes/fix-line-entry-file-population/` →
  `docs/spec/archive/2026-05-31-fix-line-entry-file-population/`
- [x] 5.4 push 归档

## 备注

- `fix` 类型，最小化模式 — 单行 IR emit 修正 + 跟随 regen + 文档同步
- DBUG / zbc 字节会 shift（更多 file str_idx 写入），但 format version 不需要 bump
  — 格式既有字段已支持此值；fixtures regen 是预期 diff
