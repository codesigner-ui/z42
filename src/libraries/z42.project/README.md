# z42.project

## 职责
项目清单 `z42.toml` 的**类型化模型**（全 z42）。作为 z42c（编译器）与 z42.build
（发布管线）**共同依赖的单一真相**：一处定义 schema，两处复用，避免模型重复与漂移。

工程配置是**确定的**——字段固定、不开放任意自定义键（含 `[platform.*]` 也用 typed
固定字段，不用开放 map）。只描述**数据形态**，不含解析逻辑（TOML → 模型 的 loader 留待实现期）。

> ⚠️ **Parked / 接口先行（2026-06-18）**：受限自举子集写法（sealed class + 构造函数、
> `bool HasX` 替 nullable、`array + count` 替泛型；无 record / 无泛型 / 无 nullable），
> 与 `src/compiler/z42c.project` 同子集，类型名对齐（`DepEntry` / `WorkspaceManifest`）便于
> 日后 z42c 直接引用本库（届时删 z42c 自带的 ProjectModel）。**暂不接入编译**（无清单）。
> schema 以 `docs/design/compiler/project.md` 为准。**当前 `Z42.Project` 与 z42c.project
> 暂并存两份**（User 决策：z42.project 按最终方案写，z42c 后续引用）。

## 核心文件
| 文件 | 段 | 职责 |
|------|----|------|
| `src/ProjectManifest.z42` | 根 | 聚合各段的完整清单（单项目） |
| `src/ProjectInfo.z42` | `[project]` | name / version / kind / entry / pack |
| `src/Sources.z42` | `[sources]` | include / exclude glob（array + count） |
| `src/BuildConfig.z42` | `[build]` | output_dir / cache_dir / dist_dir / incremental |
| `src/Profile.z42` | `[profile.*]` | pack / strip / mode / optimize / debug |
| `src/DepEntry.z42` | `[dependencies]` | 单项依赖（name / version） |
| `src/ExeTarget.z42` | `[[exe]]` | 多 exe 目标 |
| `src/PlatformSet.z42` | `[platform]` | 四平台 typed 配置集合（HasX 标志） |
| `src/iOSConfig.z42` | `[platform.ios]` | bundle_id / 能力 / team_id / device_families |
| `src/AndroidConfig.z42` | `[platform.android]` | app_id / version_code / sdk / permissions |
| `src/DesktopConfig.z42` | `[platform.desktop]` | publish_dir / icon / bundle_id |
| `src/WasmConfig.z42` | `[platform.wasm]` | title |
| `src/WorkspaceManifest.z42` | `[workspace]` | monorepo 成员（单独解析） |

## 入口点
- `ProjectManifest` —— 单项目清单根模型（含 `PlatformSet Platform`）
- `WorkspaceManifest` —— workspace 根清单模型

## 依赖关系
- 无（纯数据模型）；下游：`z42.build`、（自举后）z42c
