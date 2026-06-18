# Tasks: restructure-publish-output-dirs

> 状态：🟢 已完成 | 创建：2026-06-19 | 完成：2026-06-19

## 进度概览
- [x] 阶段 1: 容器 + 锁定
- [x] 阶段 2: 核心实现
- [x] 阶段 3: 验证与归档

## 阶段 1: 容器 + 锁定
- [x] 1.1 创建 `docs/spec/changes/restructure-publish-output-dirs/` 容器
- [x] 1.2 ACTIVE.md 登记 `compiler` 子系统占用

## 阶段 2: 核心实现
- [x] 2.1 `PathTemplateExpander.cs`：添加 `${project_name}` 别名 + `publish_dir` 到 `AllowedFieldPaths`
- [x] 2.2 `ProjectManifest.cs`：添加 `publish_dir` 到 `KnownBuildKeys`；清空 `KnownPlatformDesktopKeys`；`BuildSection` 增 `PublishDir`
- [x] 2.3 `WorkspaceManifest.cs`：`WorkspaceBuildShared` 增 `PublishDir`
- [x] 2.4 `CentralizedBuildLayout.cs`：`output_dir` 新默认值；`publish_dir` 级联；`${project_name}` 防碰撞检测
- [x] 2.5 `ResolvedManifest.cs` + `ManifestLoader.cs`：`EffectivePublishDir` 字段
- [x] 2.6 `MemberManifest.cs`：`ParseOptionalBuild` 透传 `publishDir`（遗漏修复）
- [x] 2.7 `PackageCompiler.BuildTarget.cs`：`publishDir` 参数 + `PublishToDir` 方法（复制 exe 非 stdlib 依赖）
- [x] 2.8 `PackageCompiler.cs`：`noPublish` / `publishLib` 参数 + 路由逻辑
- [x] 2.9 `WorkspaceBuildOrchestrator.cs`：`BuildOptions.NoPublish` / `PublishLib`
- [x] 2.10 `BuildCommand.cs`：`--no-publish` flag + `z42c publish` 命令 (`CreatePublish`)
- [x] 2.11 `Program.cs`：注册 `publish` 命令
- [x] 2.12 `src/libraries/z42.workspace.toml` + `src/z42c/z42.workspace.toml`：`${member_name}` → `${project_name}`
- [x] 2.13 `PolicyAndCentralizedBuildTests.cs` + `ProjectManifestTests.cs`：更新测试期望值

## 阶段 3: 验证与归档
- [x] 3.1 `dotnet build` — 无错误
- [x] 3.2 `dotnet test` — 1570/1570 全绿
- [x] 3.3 `IncrementalBuildIntegrationTests.cs`：z42.core 文件数 69 → 70（Runtime.z42 新增）
- [x] 3.4 `docs/design/compiler/project.md` 更新：四件套字段、新默认值、模板变量表、publish 行为表
