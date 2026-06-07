# Tasks: B0 — z42c 自举编译器骨架

> 状态：🟢 实现完成（GREEN gate 确认中）| 创建：2026-06-07 | 类型：架构（B 主线起点）

## 进度概览
- [x] 阶段 1: workspace + core/ir（无依赖叶子）
- [x] 阶段 2: 依赖子包（syntax/project/semantics/pipeline）
- [x] 阶段 3: driver（exe）
- [x] 阶段 3.5: 编译器 workspace 兄弟解析（根因修复，dogfood #1）
- [x] 阶段 4: xtask dispatch
- [x] 阶段 5: 文档（self-hosting.md + README + roadmap/proposal 同步）
- [x] 阶段 6: 验证（dotnet build/test ✅ 1543 + zbc/zpkg golden / cargo build ✅ / z42c 7/7 smoke ✅ / 完整 VM gate 后台确认中）

## 阶段 1: workspace + 叶子子包
- [ ] 1.1 `src/z42c/z42.workspace.toml`（members=["*"] + 拓扑序 default-members + `[workspace.project].version="0.1.0"` + output `artifacts/build/z42c/${member_name}/${profile}`）
- [ ] 1.2 `src/z42c/core/z42c.core.z42.toml`（lib，无兄弟依赖）+ `src/CoreSkeleton.z42`（`namespace Z42.Core`）
- [ ] 1.3 `src/z42c/ir/z42c.ir.z42.toml`（lib，无兄弟依赖）+ `src/IrSkeleton.z42`（`namespace Z42.IR`）

## 阶段 2: 依赖子包
- [ ] 2.1 `src/z42c/syntax/`（lib，dep `z42c.core`）+ `src/SyntaxSkeleton.z42`
- [ ] 2.2 `src/z42c/project/`（lib，dep `z42c.ir`）+ `src/ProjectSkeleton.z42`
- [ ] 2.3 `src/z42c/semantics/`（lib，dep core+syntax+ir）+ `src/SemanticsSkeleton.z42`
- [ ] 2.4 `src/z42c/pipeline/`（lib，dep core+syntax+semantics+ir+project）+ `src/PipelineSkeleton.z42`

## 阶段 3: driver
- [ ] 3.1 `src/z42c/driver/z42c.driver.z42.toml`（exe，pack=true，dep pipeline+ir+core）
- [ ] 3.2 `src/z42c/driver/src/Main.z42`（`Main()` 打印 banner + Exit(0)，无桥接）

## 阶段 3.5: 编译器 workspace 兄弟解析（根因修复，dogfood #1）
- [ ] 3.5.1 `BuildLibsDirs` 加 `workspaceLibDirs` 形参：既有扫描后按规范化 full-path 去重 + 排序追加（src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs）
- [ ] 3.5.2 `BuildTarget` 加 `workspaceLibDirs` 形参并透传 `BuildLibsDirs`
- [ ] 3.5.3 `RunResolved` 加 `workspaceLibDirs` 形参并透传 BuildTarget（src/compiler/z42.Pipeline/PackageCompiler.cs）
- [ ] 3.5.4 `WorkspaceBuildOrchestrator.Build` 收集成员 `EffectiveDistDir`（排序去重）+ `CompileMember` Func 增第 3 形参（src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs）
- [ ] 3.5.5 单测：sibling dist 透传 + 既有 mock 改 3 参（WorkspaceBuildOrchestratorTests.cs / WorkspaceFullExampleTests.cs）
- [ ] 3.5.6 dotnet build + dotnet test 全绿；直接 build src/z42c workspace 7 包全过（无需 Z42_LIBS）

## 阶段 4: xtask dispatch
- [ ] 4.1 `scripts/xtask.z42` `_build` 路由加 `compiler-z42` + `_buildCompilerZ42()`（mirror `_buildStdlibCore`）
- [ ] 4.2 `scripts/xtask.z42` `_test` 路由加 `compiler-z42` + `_testCompilerZ42()`（编译 + 断言 7 zpkg）
- [ ] 4.3 `build all` 末尾追加 compiler-z42；`_help()` 补 `build compiler-z42` / `test compiler-z42` 两行
- [ ] 4.4 重新编译 xtask.zpkg 并自验（`z42 xtask.zpkg build compiler-z42` / `test compiler-z42`）

## 阶段 5: 文档
- [ ] 5.1 `docs/design/compiler/self-hosting.md`（受限写法 / 布局 / 依赖图 / byte-identical 策略 / 无桥接 / perf gate / 1.0 切换 / dogfood 循环）
- [ ] 5.2 `docs/design/compiler/README.md` 核心文件表加 self-hosting.md
- [ ] 5.3 `src/z42c/README.md`（顶层）+ 7 个子包 README
- [ ] 5.4 `docs/roadmap.md`：`src/z42.compiler/`→`src/z42c/` + zpkg 命名 + B0 行/M10 进度
- [ ] 5.5 `docs/spec/changes/plan-0.3.x-three-streams/proposal.md`：同名替换 + zpkg 命名

## 阶段 6: 验证（GREEN）
- [ ] 6.1 `z42 xtask.zpkg build compiler-z42` → 7 zpkg 产出，无错
- [ ] 6.2 `z42 z42c.driver.zpkg` 打印 banner，exit 0
- [ ] 6.3 `z42 xtask.zpkg test compiler-z42` smoke 通过；删一个 zpkg 验证会失败
- [ ] 6.4 `z42 xtask.zpkg test`（默认 gate）全绿——无回归
- [ ] 6.5 spec scenarios 逐条覆盖确认
- [ ] 6.6 归档 + 提交（逐文件 stage，不带入 pre-existing 未跟踪 WIP）

## 备注
- 仅用已落地语言子集写占位代码；遇任何"无法表达" → 停下汇报（dogfood，禁止 workaround）。
- 工作树已有 pre-existing 未提交改动（`docs/design/compiler/project.md`、`add-tests-bench-manifest-config/tasks.md`）——非本变更，提交时**逐文件 stage**，不顺手带入。
- **dogfood #1**（workspace 兄弟解析）：User 2026-06-07 裁决根因修复（折入本 spec，非 follow-up），见阶段 3.5 + design Decision 7 + self-hosting.md。
- **目录定名**：subpackage 目录名 == `[project].name`（`z42c.core` 等），与 stdlib 约定一致，消除 `default-members`(dir 名) vs `${member_name}`([project].name) 歧义。
- **dotnet test 偶发 flake**：1543 测试中首跑曾 1 失败，后续 5 跑全绿、trx 无 failed outcome。本变更对既有测试 inert（单工程 `workspaceLibDirs=null`；orchestrator 测试 mock compile），故判定 pre-existing flake，与本变更无关。
