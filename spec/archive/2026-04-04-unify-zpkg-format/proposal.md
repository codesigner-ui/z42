# Proposal: Unify Package Format (.zpkg)

## Why

当前有三个产物格式（`.zbc` / `.zmod` / `.zbin`），职责划分如下：

- `.zbc`：单源文件编译产物（增量原子）
- `.zmod`：工程级模块清单，索引多个 `.zbc`（开发/增量形态）
- `.zbin`：工程级打包产物，内联所有 `.zbc`（发布形态）

`.zmod` 和 `.zbin` 本质是同一个"工程包"的两种存储模式，但用不同扩展名表示，导致：

1. VM 加载器需要处理三种扩展名
2. `z42.toml` 的 `emit` 配置项需要区分 `zmod` / `zbin`，语义不够内聚
3. 工具链（构建/发布/VM）都需要单独感知两种格式

**不做的后果**：随着 workspace / partial class 等特性推进，`.zmod` / `.zbin` 双轨维护成本持续累积。

## What Changes

- 引入 `.zpkg`（z42 Package）格式，合并 `.zmod` 和 `.zbin`
  - `mode: indexed` — 替代 `.zmod`（索引离散 `.zbc`）
  - `mode: packed`  — 替代 `.zbin`（内联所有 `.zbc`）
- `kind: exe | lib` 保留，区分可执行包和库包
- `[project]` 的 `[package]` section 名对齐（docs 与 code 统一用 `[project]`）
- 在 `z42.toml` 的 `[project]` / `[[exe]]` / `[[lib]]` / `[profile.*]` 中添加 `pack` 字段，控制输出模式，优先级链：`[profile.*].pack` > `[[exe]]/[[lib]].pack` > `[project].pack`
- `BuildConfig` 移除 `Emit` 字段（`.zpkg` 是唯一工程输出格式）
- VM 加载器：统一入口，只感知 `.zbc`（原子）和 `.zpkg`（工程包）

## Scope

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `z42.IR/PackageTypes.cs` | Modify | 新增 `ZpkgFile` / `ZpkgMode`；删除 `ZmodManifest` / `ZbinFile` |
| `z42.IR/ProjectTypes.cs` | Modify | 删除 `EmitKind`；`ProfileConfig` 加 `Pack`；`ProjectMeta` section 名对齐 |
| `z42.Driver/BuildCommand.cs` | Modify | 输出逻辑改为统一写 `.zpkg` |
| `z42.Driver/Program.cs` | Modify | 单文件模式 `--emit` 选项更新 |
| `z42.Build/ProjectManifest.cs` | Modify | 移除 emit 字段判断，改用 pack 解析 |
| `z42.Tests/GoldenTests.cs` | Modify | 更新类型引用和扩展名 |
| `z42.Tests/ProjectManifestTests.cs` | Modify | 更新断言 |
| `src/runtime/src/metadata/formats.rs` | Modify | 新增 `ZpkgFile` / `ZpkgMode`；删除 `ZbinFile` / `ZmodManifest` |
| `src/runtime/src/metadata/loader.rs` | Modify | 合并 `load_zmod` / `load_zbin` → `load_zpkg` |
| `src/runtime/src/metadata/mod.rs` | Modify | 更新 re-export |
| `docs/design/compilation.md` | Modify | 更新格式表、VM 加载表 |
| `docs/design/project.md` | Modify | `[package]` → `[project]`；新增 `pack` 字段说明；更新 emit 格式表 |

## Out of Scope

- `.zbc` 格式本身不变（仍为编译原子单元）
- Phase 2 的 partial class 合并逻辑不在此迭代中
- workspace 的 `[workspace.profile.*.exe/lib]` 子配置（预留设计，不实现）
- 注册中心依赖（version 约束）不在此迭代

## Open Questions

- [x] `mode` 字段值：确认用 `indexed` / `packed`
- [x] `pack` 字段放在哪一层：`[project]` + `[[exe]]` + `[profile.*]`，三层优先级
- [x] section 名：`[project]`（code 与 docs 统一）
