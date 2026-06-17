# z42.project

## 职责
项目清单 `z42.toml` 的**类型化模型**（全 z42）。作为 z42c（编译器）与 z42.build
（发布管线）**共同依赖的单一真相**：一处定义 schema，两处复用，避免模型重复与漂移。

只描述**数据形态**，不含解析逻辑（TOML → 模型 的 loader 留待实现期；本库定义模型 +
`IConfigTable` 开放段访问契约）。

> ⚠️ **Parked / 接口先行（2026-06-17）**：只定义数据记录与契约，不含解析实现。
> 故意不建 `z42.project.z42.toml`，**暂不接入编译**。schema 以 `docs/design/compiler/project.md`
> 为准；后续随 z42.toml 字段增长在此持续补充。待深加工后按 spec-first 落地。

## 核心文件
| 文件 | 段 | 职责 |
|------|----|------|
| `src/ProjectManifest.z42` | 根 | 聚合各段的完整清单（单项目） |
| `src/ProjectInfo.z42` | `[project]` | name / version / kind / entry / pack |
| `src/Sources.z42` | `[sources]` | include / exclude glob |
| `src/BuildConfig.z42` | `[build]` | output_dir / cache_dir / dist_dir / incremental |
| `src/Profile.z42` | `[profile.*]` | pack / strip / mode / optimize / debug |
| `src/Dependency.z42` | `[dependencies]` | 单项依赖（name / version） |
| `src/ExeTarget.z42` | `[[exe]]` | 多 exe 目标 |
| `src/Workspace.z42` | `[workspace]` | monorepo 成员（单独解析） |
| `src/IConfigTable.z42` | — | `[platform.*]` 等开放段的只读访问契约 |

## 入口点
- `ProjectManifest` —— 单项目清单根模型
- `IConfigTable` —— 开放段（平台配置等）的 typed getter 访问

## 依赖关系
- 无（纯数据模型）；下游：`z42.build`、（自举后）z42c
