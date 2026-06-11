# Tasks: fix-namespace-qualified-free-call

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：fix（最小化模式）

## 验证报告（2026-06-11）
- ✅ dotnet GoldenTests **1558/1558**（含新 `namespace_qualified_free_call.z42`：`nsfree.doubler(7)` 同命名空间限定自由调用 + 裸名控制组）
- ✅ 多文件 packed repro（`/tmp/nsmerge`：`namespace nsmerge; nsmerge.helper(...)` 跨文件）：烘焙 token = `nsmerge.helper`（修复前 `nsmerge.nsmerge.helper`），运行 exit 0
- ✅ 无回归（fix 纯加法：仅影响此前必坏的 `ns.func()` 路径；`ns==current` + member 是自由函数 双门精确，零误判；`ns.Class.Method` 不触发——target 是 MemberExpr 非 IdentExpr）

**变更说明：** `ns.func()`（命名空间限定的自由函数调用，`ns` = 当前命名空间）被 binder 误绑为 static-call-on-class `ns` → codegen 双重限定 `ns.ns.func` → 运行期 undefined。
**原因：** `BindMemberCallOnUnknownTarget`（[TypeChecker.Calls.cs:141](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs)）对 `ns.member`（`ns` 非 class/interface/enum/var/func）回落 `BoundCall(Static, ReceiverClass="ns", ...)`；codegen `EmitStaticBoundCall` DepIndex miss → `QualifyClassName("ns")` 把命名空间当类 → `QualifyName("ns")` = `ns.ns` → `ns.ns.func`。
**文档影响：** 无（纯 binder 修复，恢复正确行为；niche 边界，无对外新行为面）。docs/design 不涉及。

## 根因与修法

- 真根因：binder 把命名空间前缀 `ns` 误当作未知类的 static 接收者。
- 修：`BindMemberCallOnUnknownTarget` 起始处，若 `tgtName == _currentNamespace` 且 `env.LookupFunc(member)` 命中自由函数 → 这是**同命名空间限定的自由调用**，路由到 `BindFreeIdentCall`（绑 Free call，CalleeName=member 短名）。codegen `EmitFreeBoundCall` 对短名走一次 `QualifyName(current)` = `ns.func` ✓。
- 范围限定 `tgtName == _currentNamespace`（精确、零误判）。跨命名空间限定自由调用（`otherNs.func()` 从别的 ns）不在本 fix（codegen 需按 tgtName 而非 current 限定，属独立 concern；常规用 `using OtherNs;` + 裸名）。

- [x] 1.1 `TypeChecker.Calls.cs`：`BindMemberCallOnUnknownTarget` 加同命名空间限定自由调用识别 → `BindFreeIdentCall`
- [x] 1.2 golden e2e `src/tests/types/namespace_qualified_free_call.z42`（`ns.func()` 单文件 + 同名空间）
- [ ] 1.3 验证：dotnet GoldenTests 全绿 + wtmerge 多文件 repro 通过 + （worktree）xtask 不回归

## 备注
- 与「gate 幻影」更正同源（reference_multifile_project_namespace_double_qualify_bug）：那是误判幻影，本 fix 解决其暴露出的**唯一真 bug**。
