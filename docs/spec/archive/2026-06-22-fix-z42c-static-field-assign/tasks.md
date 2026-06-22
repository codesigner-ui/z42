# Tasks: fix-z42c-static-field-assign

> 状态：🟢 已完成 | 完成：2026-06-22

**变更说明：** z42c `ExprEmitter._emitAssign` 只处理 `BoundIdent` / `BoundMember` /
`BoundIndex` 的 LHS，**无 `BoundStaticGet` 分支**。赋值到限定静态字段
`Class.staticField = v`（LHS 绑为 `BoundStaticGet`）落到末尾 `return val` → **静默丢弃**
（函数体空）。→ 静态字段 **写**不持久（`Log.SetMinLevel(3)` 后 `_minLevel` 仍默认）。
读（`static_get`）与 `__static_init__`（`static_set`）正常，仅显式赋值丢失。
**原因：** dogfood S3——z42c-built z42.diagnostics 3 test（min-level/color）失败：
`SetMinLevel` 写 `Log._minLevel` 丢失。最小复现：`Counter._v = x` 体空（IR `entry: ret`）。
**文档影响：** 无（行为对齐 C#）。

- [x] 1.1 `_emitAssign`：加 `a.Target is BoundStaticGet` 分支 → `StaticSetInstr(QualifyClass(ClassName)+"."+FieldName, val)`
- [x] 1.2 验证：最小复现（Counter._v 赋值后读==42，IR 现含 static_set）；z42c-built DiagnosticsFilter 4/4；compiler-z42 byte-identical 7/7 + 17 units 不回归
- [x] 1.3 归档

## 附：blake3 多块「codegen bug」= 误报（无 codegen bug）
S3 第 8 个失败（blake3 多块回归测试）经查**非 z42c codegen bug**：z42c-built == C#-built blake3 全尺寸一致（64/200/1024/1025/2048/2049）。根因 = 我的回归测试 golden 误写（`a4283937…` 应为 `5f4d72f4…`，bad capture）。已改正 golden（z42-blake3 == nuget via 20KB build_id 已证）。该 golden 误值在默认 gate（C#-built）也会失败——一并修。

## 备注
- 这是 replace-csharp S3 codegen bug #2（最后一类，diagnostics 3 test）；与 blake3 多块 codegen 并列。
- 影响面：任何 `Class.staticField = v` 显式赋值（z42c 自身包静态字段多只读 → 未触发；compiler-z42 7/7 不受影响）。
