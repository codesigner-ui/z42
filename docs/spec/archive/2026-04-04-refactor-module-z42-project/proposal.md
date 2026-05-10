# Proposal: refactor-module-z42-project

## Why

`z42.Build` 这个名字与 `dotnet build` 产生认知冲突，容易让人误以为这是构建系统入口。实际上该模块只做两件事：读取项目清单（`.z42.toml`）和描述编译产物格式（`.zbc` / `.zpkg`）——二者都是"项目层面的契约"，与"执行构建"无关。

同时 `z42.IR` 夹带了两块与 IR 无关的内容：
- `PackageTypes.cs`（`ZbcFile`、`ZpkgFile` 等产物类型）
- `ProjectTypes.cs`（`Z42Proj`、`Z42Sln`——已确认为死代码，无任何调用方）

以及一个从未被实际调用的依赖：Tomlyn 1.2.0。

不改的话：
- 模块职责边界模糊，新人看到 `z42.IR` 里有 `ZpkgFile` 会困惑
- `z42.IR` 携带死依赖，影响构建速度
- 模块命名持续造成误导

## What Changes

- 将 `z42.Build/` 重命名为 `z42.Project/`，命名空间 `Z42.Build` → `Z42.Project`
- 将 `z42.IR/PackageTypes.cs`（产物类型）迁移到 `z42.Project/`
- 删除 `z42.IR/ProjectTypes.cs`（死代码）
- 删除 `z42.IR.csproj` 中的 Tomlyn 1.2.0 依赖
- 更新所有引用方的 using / ProjectReference

## Scope

| 文件 / 模块 | 变更类型 | 说明 |
|-------------|---------|------|
| `z42.Build/` → `z42.Project/` | 重命名 | 目录 + csproj + namespace |
| `z42.IR/PackageTypes.cs` | 迁移 | 移入 z42.Project，namespace Z42.IR → Z42.Project |
| `z42.IR/ProjectTypes.cs` | 删除 | 死代码，无调用方 |
| `z42.IR/z42.IR.csproj` | 修改 | 删除 Tomlyn 1.2.0 |
| `z42.slnx` | 修改 | 更新 Project 路径 |
| `z42.Driver/z42.Driver.csproj` | 修改 | ProjectReference 路径 |
| `z42.Driver/BuildCommand.cs` | 修改 | using Z42.Build / Z42.IR → Z42.Project |
| `z42.Tests/z42.Tests.csproj` | 修改 | ProjectReference 路径 |
| `z42.Tests/ProjectManifestTests.cs` | 修改 | using Z42.Build → Z42.Project |
| `z42.Tests/GoldenTests.cs` | 修改 | 新增 using Z42.Project（ZbcFile 等） |
| `src/compiler/README.md` | 修改 | 更新模块描述 |

## Out of Scope

- 编译器核心（Lexer / Parser / TypeCheck / Codegen）不动
- IR 指令集、zbc 格式、VM 不动
- `z42.Project` 内部逻辑不变，只改命名和归属

## Open Questions

无——方案已在对话中与 User 确认。
