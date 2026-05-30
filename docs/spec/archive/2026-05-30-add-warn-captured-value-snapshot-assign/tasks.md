# Tasks: W0604 — captured value-snapshot assign warning

> 状态：🟢 已完成 | 创建：2026-05-30 | 归档：2026-05-30 | 类型：lang（新 compiler 诊断）

## 进度

- [x] 1.1 MODIFY `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` — add `CapturedValueSnapshotAssign = "W0604"`
- [x] 1.2 MODIFY `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs` — BindAssign 末尾 check + Warning emit
- [x] 1.3 NEW `src/compiler/z42.Tests/WarnCapturedValueSnapshotAssignTests.cs` — 7 xUnit scenarios per spec
- [x] 1.4 PRE-IMPL stdlib scan: `./scripts/build-stdlib.sh 2>&1 | grep -c W0604` 记基线
- [x] 1.5 POST-IMPL stdlib scan: 同上 + 逐条审视真伪
- [x] 1.6 若 stdlib 有 W0604：fix offending 文件改 class wrap / array cell
- [x] 1.7 MODIFY `docs/design/language/closure.md` — 加 §4.5 编译期提示 + W0604 引用
- [x] 1.8 GREEN：dotnet build + dotnet test + build-stdlib + test-all.sh
- [x] 1.9 归档 → `docs/spec/archive/2026-05-30-add-warn-captured-value-snapshot-assign/`
- [x] 1.10 commit + push（仅 W0604 scope；不裹进其他 session changes）

## 备注

- W0604 选自 W06xx semantic-edge warning 段（W0603 紧邻）
- 不引入 suppress 机制（out of scope）；intentional local-effect case 留
  `add-w0604-suppress` follow-up
- 不强制 error（pre-1.0 决定保 warning；§4.1 是 design feature 不是 bug）
- 若 stdlib 大量 hits 暗示 spec §4.1 在生态内被广泛误解，停下汇报，
  请 User 裁决（升级 error / 收紧 §4.1 范围 / 加 suppress 机制）

## 实施备注（2026-05-30）

- BindAssign 末尾插入 8 行 check：`target is BoundCapturedIdent ci
  && !Z42Type.IsReferenceType(ci.Type)` → `_diags.Warning(W0604, …,
  assign.Span)`. 复用与 TypeChecker.Exprs.cs:410-412 决定 `BoundCaptureKind`
  完全相同的判据，无需新增 IR pass / 改 BoundCapturedIdent shape.
- W0604 catalog entry 必须加（FormatInvariantTests 强制每个 code 有
  DiagnosticCatalog 条目，否则 `z42c explain W0604` 无文档）。
- xUnit 7/7 GREEN：bool / int compound / reference field / array cell /
  lambda-local / top-level / nested-lambda. 覆盖 §4.1 vs §4.2 vs §4.4 全
  matrix.
- **Stdlib 扫描结果：0 hits**。整个 stdlib 没有一个文件命中 W0604 —
  说明 §4.1 在生态内已正确实践（要么不写、要么走 class wrap / array cell）。
  也意味着本 spec 是纯"防 future regression"的护栏，不需要回填旧代码。
- 路上踩了 pre-existing E0420 in `scripts/build-stdlib.z42` —
  concurrent build session 之间 zpkg 偶发损坏导致 `catch (Exception e)`
  在 type-check 时 Exception 类找不到。验证：stash 我的全部 W0604
  改动后 E0420 仍现 ⇒ 与 W0604 无关；clean `rm -rf artifacts/build/libs +
  build/libraries/*/release` 后再 `./scripts/build-stdlib.sh` 恢复。
- closure.md §4.5 新加一节链接 W0604 + 两条修复路（class wrap /
  array cell），与 §4.4 文档无重叠。
