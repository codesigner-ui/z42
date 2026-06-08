# z42c.project

## 职责
镜像 C# [z42.Project](../../compiler/z42.Project/README.md)：项目清单（`.z42.toml` 解析 / 源文件发现 / zpkg builder）。**incr 1-2：单清单 + workspace 加载器已落地**（用 stdlib `Std.Toml` 解析，区别于 C# 用 Tomlyn）。占位 `ProjectSkeleton` 暂留（semantics/pipeline 仍引用）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/ProjectModel.z42` | 清单模型：`ProjectManifest`（name/version/kind/entry/pack + sources + deps）/ `WorkspaceManifest`（members/exclude/default-members + 共享 version·license + build 路径模板 + workspace deps）/ `DepEntry` / `DepList` |
| `src/ManifestLoader.z42` | `.z42.toml`→`ProjectManifest`（`ParseText`/`Load`）+ `z42.workspace.toml`→`WorkspaceManifest`（`ParseWorkspaceText`/`LoadWorkspace`）（用 `Std.Toml.TomlValue`）|
| `src/PathTemplate.z42` | 路径模板展开：`PathTemplate.Expand(tpl, ctx)`（`${member_name}`/`${profile}`/`${output_dir}` 等 + `$$`→`$`）|
| `src/ProjectSkeleton.z42` | **过渡占位**：semantics/pipeline 仍引用；各自移植时移除 |

## 入口点
`Z42.Project.ManifestLoader`：`.ParseText(text)`/`.Load(path)` → `ProjectManifest`；`.ParseWorkspaceText(text)`/`.LoadWorkspace(path)` → `WorkspaceManifest`。

## 依赖关系
→ z42c.ir, z42.toml（stdlib）。Std / Std.IO 自动可用。

## 待移植（后续增量，见 port-z42c-project tasks）
workspace 继承 / include 链 / policy / `[[exe]]` targets / profiles / tests·bench / 路径模板 / 源文件 glob 发现；
**ZpkgReader / ZpkgWriter（byte-identical，依赖 z42c.ir 的 ZbcFile）** / ZpkgBuilder。
