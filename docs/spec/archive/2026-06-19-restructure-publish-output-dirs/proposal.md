# restructure-publish-output-dirs

## 问题

1. **源码目录污染**：单工程模式下 `output_dir` 默认值为 `memberDir`（项目本身），导致 `dist/`、`.cache/` 直接出现在源码目录内。
2. **Exe 依赖缺失**：编译出的 exe `.zpkg` 运行时找不到非标准库依赖（需手设 `Z42_LIBS` 环境变量），因为没有机制将依赖复制到产物目录旁边。
3. **`publish_dir` 分散**：`publish_dir` 仅存在于 `[platform.desktop]`（apphost 专用），无法用于通用 publish 场景（lib 发布、exe 发布）。

## 解决方案

### 1. `output_dir` 默认值对齐 .NET artifacts 模式

- **workspace 模式**：`${workspace_dir}/artifacts/${project_name}/${profile}`
- **单工程模式**：`${workspace_dir}/artifacts/${profile}`（无 project_name 层，因为已在项目目录内）

`.NET 8 artifacts/bin/` 模式的灵感：构建产物统一归集到 workspace 根的 `artifacts/` 子目录，不污染源码。

### 2. 统一 `publish_dir`

- 在 `[build]` 节添加 `publish_dir`（默认 `${output_dir}/publish`）
- 从 `[platform.desktop]` 移除 `publish_dir`（toolchain launcher 改读 `[build].publish_dir`）

### 3. Exe build 自动 publish

- `z42c build`（exe 类型）：编译后自动将 exe.zpkg + .zsym（若存在）+ 非标准库依赖复制到 `publish_dir`
- `z42c build`（lib 类型）：仅输出到 `dist_dir`，不自动 publish
- `z42c build --no-publish`：跳过 publish 步骤
- `z42c publish`：显式 publish 命令，对 exe = artifact + deps，对 lib = 仅 artifact

### 4. 模板变量

- 新增 `${project_name}` 作为 `${member_name}` 的别名（更清晰的命名）
- 将现有 workspace.toml 中的 `${member_name}` 迁移到 `${project_name}`

## Scope

子系统：`compiler`

文件列表：
- `src/compiler/z42.Project/PathTemplateExpander.cs`
- `src/compiler/z42.Project/ProjectManifest.cs`
- `src/compiler/z42.Project/CentralizedBuildLayout.cs`
- `src/compiler/z42.Project/ResolvedManifest.cs`
- `src/compiler/z42.Project/ManifestLoader.cs`
- `src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs`
- `src/compiler/z42.Pipeline/PackageCompiler.cs`
- `src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs`
- `src/compiler/z42.Driver/BuildCommand.cs`
- `src/libraries/z42.workspace.toml`
- `src/z42c/z42.workspace.toml`

不在本次 Scope：`src/toolchain/launcher/core/launcher_export.z42`（独立 toolchain change）。
