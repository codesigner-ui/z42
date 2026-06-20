# Tasks: fix-golden-harness-interp-pin

> 状态：🟢 已完成 | 创建：2026-06-21 | 完成：2026-06-21

**变更说明：** C# GoldenTests.RunMatchesExpected 调 z42vm 时显式固定 `--mode interp`。
**原因：** `default-jit-mode` 把 z42vm CLI 默认从 interp 翻转到 jit 后，这个**编译器侧参考 harness**（不带 `--mode`）静默改跑 jit。它的 expected_output.txt 全是 interp 下采集的，且部分用例（`src/tests/gc/gc_oom_exception`，带 `interp_only` 标记）语义上仅 interp 成立 → macos-15 CI `FieldSet: expected object, got Null` exit 1。jit 覆盖是 xtask VM golden runner 的职责（它认 `interp_only` 标记），C# harness 应锁定 interp 参考语义。
**子系统：** `compiler`（ACTIVE.md 登记）
**文档影响：** 无外部行为/机制变更（恢复 jit-default 前的既有契约 + 代码内注释说明）。

- [x] 1.1 `src/compiler/z42.Tests/GoldenTests.cs` RunMatchesExpected：ArgumentList 加 `--mode interp` + 注释说明为何 pin
- [x] 1.2 验证：`dotnet test --filter ...gc_oom_exception` → Passed（debug z42vm + C# 编译器权威路径）
- [x] 1.3 验证：full `dotnet test z42.Tests.csproj` 全绿（1571/1571，confirm interp pin 未破坏其它 golden）
- [x] 1.4 归档 + commit + push（CI 重新转绿确认）
