# Tasks: Restructure build output directories

> 状态：🟢 已完成 | 创建：2026-06-06 | 归档：2026-06-06 | 类型：lang / 接口契约（manifest schema 变更）

## 实施结果摘要

- `[build]` schema 三件套（`output_dir` / `cache_dir` / `dist_dir`）落地，老 `out_dir` 删除；KnownBuildKeys 同步触发 WS008 unknown-key + Levenshtein 建议 `dist_dir`（额外加 `KnownRenames` 表覆盖 Levenshtein 距离过大的迁移情况）
- `WorkspaceBuildShared` 同三字段 raw-nullable + effective 级联默认 `output_dir → workspace_root` / `cache_dir → ${output_dir}/.cache` / `dist_dir → ${output_dir}/dist`，member 任一字段 unset 沿继承链取值
- `CentralizedBuildLayout` 重写为 workspace + single-project 双模统一入口，effective 三路径直填 `ResolvedManifest.EffectiveOutputDir / EffectiveCacheDir / EffectiveDistDir`（替代了原 `EffectiveOutDir`）
- 单工程模式（无 z42.workspace.toml）现也走 layout 计算，`PackageCompiler.Run` 不再自行拼接路径
- `PolicyFieldPath` / `PolicyEnforcer` 受管字段同步三件套；老 `build.out_dir` 拼写错误（WS011）通过 KnownRenames 建议 `build.dist_dir`
- 仓库内 11 个 `.z42.toml` / `.z42-workspace.toml` 全部迁移（删除默认值或显式改 `dist_dir`）
- `docs/design/compiler/project.md` 全面重写 `[build]` 段 + 新增 `[sources]` glob 示例段（iteration 2 文档化）
- GREEN：dotnet test 1490/1490 通过；`xtask build stdlib` 22/22 zpkg 成功

## 进度概览
- [ ] 阶段 1: schema 层（BuildSection / WorkspaceBuildSection / KnownBuildKeys 同步）
- [ ] 阶段 2: effective 计算（ResolvedManifest / CentralizedBuildLayout 三路径）
- [ ] 阶段 3: 消费方迁移（PolicyFieldPath / Driver / PackageCompiler）
- [ ] 阶段 4: 仓库内 *.z42.toml 全量 rename + 单元/集成测试
- [ ] 阶段 5: 文档同步（project.md / workspace.md，iteration 2 sources glob 示例）
- [ ] 阶段 6: GREEN + 归档 + commit + push

## 阶段 1: schema 层
- [ ] 1.1 `BuildSection`：`OutDir` → `DistDir`；加 `OutputDir` / `CacheDir`（全部 `string?` optional）；默认 ctor + `KnownBuildKeys` 同步删 `out_dir` 加新三字段
- [ ] 1.2 `WorkspaceBuildSection`：同 BuildSection 三字段对齐；workspace 的 `out_dir = "dist"` / `cache_dir = ".cache"` 默认值移除（统一改为 unset = null，effective 计算在 layout）
- [ ] 1.3 `ParseBuild` / `ParseWorkspaceBuild`：读取新字段；老 `out_dir` 不再 fallback（通过 KnownBuildKeys 删掉后，hygiene 扫描自然报 WS008）

## 阶段 2: effective 计算
- [ ] 2.1 `ResolvedManifest` 加 `EffectiveOutputDir` / `EffectiveCacheDir` / `EffectiveDistDir`（删 `EffectiveOutDir`）；Resolve(...) 内 default-fill 逻辑（output_dir → dirname(toml)；cache → ${output_dir}/.cache；dist → ${output_dir}/dist）
- [ ] 2.2 `CentralizedBuildLayout`：`EffectiveOutDir` 字段 rename 到 `EffectiveDistDir`；加 `EffectiveOutputDir` + `EffectiveCacheDir`；模板变量插值加入 `${output_dir}`；member 继承 workspace 三字段的覆盖矩阵
- [ ] 2.3 单元测试：`ResolvedManifestTests` / `CentralizedBuildLayoutTests` 默认值 + 覆盖矩阵全覆盖

## 阶段 3: 消费方迁移
- [ ] 3.1 `PolicyFieldPath.KnownFields`：删 `build.out_dir`，加 `build.dist_dir` / `build.output_dir` / `build.cache_dir`；GetValue switch 同步；测试更新
- [ ] 3.2 `Driver`：单文件编译路径合成 synthetic BuildSection（`OutputDir = cliOverride ?? dirname(src)`）；`--output-dir` CLI 标志（如已有则改语义，否则新增）；多文件/manifest 路径走 ResolvedManifest 的 effective 三路径
- [ ] 3.3 `PackageCompiler`：所有 `.zbc` 写入路径改读 `EffectiveCacheDir`；所有 `.zpkg` / exe 写入路径改读 `EffectiveDistDir`；删除现存 `${out_dir}/.cache` 硬编码

## 阶段 4: 仓库内 toml 迁移 + 测试
- [ ] 4.1 `find . -name '*.z42.toml' -o -name '*.z42-workspace.toml'`：所有 `out_dir = "..."` 字段处理 —— 命中默认（`"dist"`）删除；显式覆盖改 `dist_dir = "..."`
- [ ] 4.2 `ProjectManifestTests`：旧 OutDir 测试更新；加 OutputDir / CacheDir / DistDir 默认 + 覆盖测试；WS008 老 `out_dir` 警告测试
- [ ] 4.3 `WorkspaceManifestTests`：同上 workspace
- [ ] 4.4 `CentralizedBuildLayoutTests`：EffectiveOutDir 引用全替；加 EffectiveOutputDir / EffectiveCacheDir 断言；`${output_dir}` 模板插值测试
- [ ] 4.5 `PolicyFieldPathTests`：新 KnownFields；旧字段不再 known

## 阶段 5: 文档同步
- [ ] 5.1 `docs/design/compiler/project.md`：`[build]` schema 章节重写 —— 三字段说明 + 默认值表 + 迁移说明 + 一个 RAM disk cache 示例
- [ ] 5.2 同文档加 `[sources]` 章节：include / exclude glob 至少 3 个示例 + `[[exe]].src` 覆盖关系 + 明确"无 negation pattern" + 默认 exclude 为空（iteration 2 文档化部分）
- [ ] 5.3 `docs/design/compiler/workspace.md`：workspace [build] 三字段 + member 继承覆盖矩阵
- [ ] 5.4 `docs/roadmap.md`：本 spec 归档后从 Deferred Backlog Index 移除（如果有）

## 阶段 6: GREEN + 归档
- [ ] 6.1 `dotnet build src/compiler/z42.slnx` 无错
- [ ] 6.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [ ] 6.3 `z42 xtask.zpkg test compiler` + `z42 xtask.zpkg test stdlib z42.core` 通过（验证 stdlib 仍能编译）
- [ ] 6.4 spec scenarios 逐条覆盖确认表
- [ ] 6.5 `docs/spec/changes/restructure-build-output-dirs/` → `docs/spec/archive/2026-06-06-restructure-build-output-dirs/`
- [ ] 6.6 commit（含 `.claude/` / `docs/spec/`）+ push

## 备注

- 阶段 4 的 toml 全量 rename 是机械工作，可能涉及 ~30 个文件（22 stdlib + launcher + xtask + examples）；批量 sed 不可行（字段在不同段且可能未设），需逐文件 grep + 替换。
- iteration 2 文档化与 iteration 1 schema 改动 bundled 在同一 spec，因为 `[sources]` glob 示例本来就在 project.md 同一章节，分开做反而需要两次改同文档。
- WS008 hygiene warning 自动 cover 老 `out_dir` 迁移路径（KnownBuildKeys 删掉后自然识别为 unknown-key + Levenshtein 命中 `dist_dir`）—— 不需要额外手写 deprecation 逻辑。
