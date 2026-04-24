# Tasks: refactor-module-z42-project

> 状态：🟢 已完成 | 创建：2026-04-04

**变更说明：** 将 `z42.Build` 重命名为 `z42.Project`，并将 `z42.IR` 中的产物类型迁入，删除死代码与死依赖。
**原因：** 模块命名误导，IR 夹带与自身无关的类型和依赖。
**文档影响：** `src/compiler/README.md`（模块说明）

---

## 进度概览

- [ ] 阶段 1：创建 z42.Project 模块
- [ ] 阶段 2：清理 z42.IR
- [ ] 阶段 3：更新引用方
- [ ] 阶段 4：验证

---

## 阶段 1：创建 z42.Project 模块

- [ ] 1.1 新建 `z42.Project/z42.Project.csproj`（依赖 Tomlyn 2.3.0 + FileSystemGlobbing + z42.IR）
- [ ] 1.2 新建 `z42.Project/ProjectManifest.cs`（从 z42.Build 复制，namespace `Z42.Build` → `Z42.Project`）
- [ ] 1.3 新建 `z42.Project/PackageTypes.cs`（从 z42.IR 复制，namespace `Z42.IR` → `Z42.Project`）

## 阶段 2：清理 z42.IR

- [ ] 2.1 删除 `z42.IR/ProjectTypes.cs`（死代码）
- [ ] 2.2 删除 `z42.IR/PackageTypes.cs`（已迁移）
- [ ] 2.3 `z42.IR/z42.IR.csproj` 删除 `Tomlyn 1.2.0` 依赖
- [ ] 2.4 删除 `z42.Build/` 目录（旧模块）

## 阶段 3：更新引用方

- [ ] 3.1 `z42.slnx`：`z42.Build/z42.Build.csproj` → `z42.Project/z42.Project.csproj`
- [ ] 3.2 `z42.Driver/z42.Driver.csproj`：ProjectReference 路径更新
- [ ] 3.3 `z42.Driver/BuildCommand.cs`：`using Z42.Build` → `using Z42.Project`；`ZbcFile`/`ZpkgFile` 等从 `Z42.IR` 改为 `Z42.Project`
- [ ] 3.4 `z42.Tests/z42.Tests.csproj`：ProjectReference 路径更新
- [ ] 3.5 `z42.Tests/ProjectManifestTests.cs`：`using Z42.Build` → `using Z42.Project`
- [ ] 3.6 `z42.Tests/GoldenTests.cs`：新增 `using Z42.Project`（`ZbcFile`、`ZpkgFile`、`ZpkgKind`、`ZpkgMode`、`ZpkgExport`）
- [ ] 3.7 `src/compiler/README.md`：更新 z42.Build → z42.Project 相关描述

## 阶段 4：验证

- [ ] 4.1 `dotnet build src/compiler/z42.slnx` —— 零错误零警告
- [ ] 4.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 全绿

## 备注

- `ZbcRoundTripTests.cs` 不需要修改（只用 `Z42.IR.BinaryFormat`，不用 PackageTypes）
- `Program.cs` 不需要修改（不直接使用 ZbcFile/ZpkgFile）
