# Tasks: Split FunctionEmitter.cs (Hard Limit Violation)

> 状态：🟢 已完成 | 创建：2026-05-12 | 完成：2026-05-12
> 类型：refactor（最小化模式 — 文件级拆分）

## 实施备注

- `FunctionEmitter.cs` 559 → 280 LOC（双满足 300 软限 + 500 硬限）
- `FunctionEmitter.StaticInit.cs` 185 LOC（含 nested `ClassRefScanner`）
- `FunctionEmitter.Helpers.cs` 123 LOC
- 零行为变更 — 拆分仅按职责分离 partial 文件
- 全绿验证：./scripts/test-all.sh 6 stage（dotnet build / cargo build / dotnet test 1233/1233 / VM golden 320/320 / cross-zpkg 1/1 / stdlib 6/6 lib）

**变更说明**：`FunctionEmitter.cs` 559 LOC 超 500 硬限。按职责拆为 3 个 partial 文件，零行为变更。

**原因**：`.claude/rules/code-organization.md` 硬限 — C# 文件 500 行必须拆分。

**Scope（4 文件）**
- MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs) — 保留 fields / ctor / entry points (`EmitMethod` + `EmitFunction`)
- NEW [src/compiler/z42.Semantics/Codegen/FunctionEmitter.StaticInit.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.StaticInit.cs) — `EmitStaticInit` + `TopologicalSortStaticInits` + `CollectClassRefs` + nested `ClassRefScanner`
- NEW [src/compiler/z42.Semantics/Codegen/FunctionEmitter.Helpers.cs](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.Helpers.cs) — block 管理 / line 跟踪 / TypeName / WriteBackName / SnapshotLocalVarTable / ToIrType ×2
- MODIFY [src/compiler/z42.Semantics/Codegen/README.md](../../../../src/compiler/z42.Semantics/Codegen/README.md) — 同步新 partial 文件

**文档影响**：仅 README 同步（无外部行为变更，无 design doc 更新）。

## 进度概览

- [x] 1.1 抽出 StaticInit.cs（含 nested `ClassRefScanner`）
- [x] 1.2 抽出 Helpers.cs（block + line + 类型映射 + WriteBackName + 局部变量表）
- [x] 1.3 README.md 加新两个文件
- [x] 1.4 GREEN 验证：./scripts/test-all.sh 6 stage 全绿
- [x] 1.5 归档 → `docs/spec/archive/2026-05-12-split-function-emitter/`
