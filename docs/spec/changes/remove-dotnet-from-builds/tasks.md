# Tasks: remove-dotnet-from-builds (replace-csharp S5 Phase A)

> 状态：🟡 进行中 | 创建：2026-06-22
> 子系统：`toolchain`
> 变更说明：把 BUILD 调用点从 C# driver（dotnet）切到 z42c（z42vm + z42c.driver.zpkg），
> 使构建不再依赖 dotnet。保留 C# 作 byte-identical oracle 门（Phase B/C 再替换 + 删 C#）。
> 原因：replace-csharp S5；User 选 Phase A（先切构建、gate 不可逆的删除）。
> 文档影响：self-hosting.md（S5 Phase A）。

## 进度概览
- [x] A1 生产 stdlib flip C#-free（warm：种子存在用 z42c 自建；cold：C# 兜底）
- [ ] A2 其余 build 站点切 z42c（xtask self-build / package / regen / bench / test-unit）
- [ ] A3 bootstrap 提供种子（S4 download）→ CI 默认 warm C#-free（非 cold C#）
- [ ] A4 文档 + 归档

## A1 — 生产 stdlib flip C#-free ✅
- [x] `_buildCompilerZ42ViaZ42c`（xtask_compiler_z42.z42）：用现有 z42c.driver.zpkg 作种子，
  单 toml 拓扑逐成员重建 z42c（accumulate fresh siblings），写 canonical dist，**无 dotnet**。
- [x] `_buildStdlibCore`（xtask_stdlib.z42）：warm（z42c.driver.zpkg + stdlib 存在 && !Z42_STDLIB_CSHARP_SEED）
  → `_buildCompilerZ42ViaZ42c`（C#-free）；cold → `_csharpBuildStdlibSeed` + `_buildCompilerZ42`（C# 兜底）。
- [x] 验证：warm `xtask build stdlib` + dotnet PATH-stub 屏蔽 → 22/22 stdlib 成功，零 dotnet 调用。
- [x] CI 安全：gate 的 flip 单次 cold（fresh checkout 无种子）→ C# 路径不变；oracle 自建 C# z42c 不受影响。

## 备注
- Phase B（替 oracle：byte-identical-vs-C# → 自举不动点；dotnet test 1571 去留）+ C（删 src/compiler/）
  不可逆 → 待 User 裁决后单独做。
- A1 默认 warm→C#-free，但 CI fresh = cold → 仍 C#；A3（bootstrap 种子）后 CI 才默认 C#-free。
