# Tasks: z42c-build-workspace

> 状态：🟢 已完成 | 创建：2026-06-21 | 完成：2026-06-21
> 子系统：`z42c`（ACTIVE.md 登记）

## Milestone 1 结论（2026-06-21）
- ✅ `build --workspace` 核心：成员发现 + 拓扑序 + build loop，z42c 自己 7 包全建（core→ir→project→syntax→semantics→pipeline→driver）。
- ✅ 编排正确：workspace 输出 == z42c 单包 build 输出（z42c.core 26909==26909）。
- ✅ 功能正确：z42c-build 出的 z42c.driver 当编译器可跑（--emit-zbc OK）。
- ✅ compiler-z42 gate 绿（7/7 emit byte-identical，无回归）。
- ⚠️ 限制（M1）：需显式 `--output-dir <flat>` + 调用方预 seed stdlib（单 libsDir）。
- 📌 两个 pre-existing 字节差异（非本 change）：z42c `build` 整包 != C#（DEPS/TSIG 从未字节门控，gate 只验 --emit-zbc）；emit 无 DBUG（设计）。

## Milestone 2 结论（2026-06-21，drop-in 替换 stdlib build）
- ✅ **per-member 布局**：无 `--output-dir` 时按 `[workspace.build].output_dir` 模板（`PathTemplate.Expand` + `${project_name}` 别名补齐）展开 → 各成员产物落 `artifacts/build/libraries/<name>/<profile>/dist/`，镜像 C# `CentralizedBuildLayout.ResolveWorkspace`。
- ✅ **多 libsDir**：`DepScan.ScanDirs` 多目录合并 + **basename first-wins 去重**（成员 dist 与 Z42_LIBS 外部 stdlib 同名包只扫一次，避免 DepIndex 重复 AddModule → 歧义剔除）。镜像 C# `workspaceLibDirs`（成员 EffectiveDistDir 透传）。
- ✅ **验证**：z42c（interp）`build --workspace --release`（无 --output-dir，cwd=src/libraries）→ 22 库全建到 per-member 布局 + 功能正确（z42c-built stdlib 跑 z42c --emit-zbc OK，3782B .zbc）。
- ✅ **Milestone 1 无回归**：compiler-z42 gate byte-identical 7/7（单目录走 ScanDirs 退化，去重 no-op）。
- ✅ **拓扑单测**：z42c.pipeline/tests/workspace_topo 3/3（chain/fan-out/cycle）。
- 📌 整包 stdlib zpkg vs C# 字节差异是 pre-existing（DEPS provider env-artifact / TSIG / IMPL）；gate 只验 --emit-zbc + 功能，不追整包 byte-identical（见 memory project_csharp_to_z42c_replacement）。这是 replace-csharp-compiler **S3 的能力前置**。

## 进度概览
- [x] 1. 成员发现 + dep 图 + 拓扑（WorkspaceBuild.z42）
- [x] 2. build loop 编排
- [x] 3. Main.z42 接线 `build --workspace` + `clean`
- [x] 4. 验证：byte-identical（z42c 7 包；stdlib 22 库 per-member 布局 + 功能正确）

## 1. WorkspaceBuild.z42（端口 C# orchestrator + graph）
- [x] 1.1 `DiscoverMembers(wsDir, WorkspaceManifest)`：members `*` glob → 各成员 toml + ProjectManifest（name + deps），flat 平行数组
- [x] 1.2 workspace 内成员间的边（IndexOfName 过滤外部 dep）
- [x] 1.3 环检测：O(N²) 拓扑「无进展即环」（等价 DFS 三色，更简）
- [x] 1.4 `TopoOrder`：O(N²) 层式发射（就绪集按 name Ordinal 排序 = C# TopologicalLayers 层内 name-sort 同序）
- [x] 1.5 单测：链/扇出/环 3 用例（z42c.pipeline/tests/workspace_topo/）—— ✅ 3/3（test_topo_chain/fanout/cycle_throws）

## 2. build loop
- [x] 2.1 `_buildWorkspace(isRelease, outputDir)`：拓扑序逐成员 `_build`；成员 fail→return（fail-fast，blocked 传播留 follow-up）
- [x] 2.2 共享 flat dist：各成员产物落 outputDir（= libsDir），deps-first 解析兄弟

## 3. Main.z42 接线
- [x] 3.1 `_build` 加 libsDirOverride 参数（"" → Z42_LIBS env，workspace 传 outputDir）
- [x] 3.2 `build` 分支识别 `--workspace`（ai from 1）→ `_buildWorkspace`；`_findWorkspaceToml` 向上搜
- [x] 3.3 `clean` 命令（删 <dir>/{dist,cache}）

## 4. 验证
- [x] 4.1 重建 z42c → `z42c build --workspace` 编 z42c 7 包（C# 驱动 7 members 全建）
- [x] 4.2 `./xtask test compiler-z42` byte-identical 仍 7/7（exit 0，无回归）
- [x] 4.3 `z42c build --workspace` 编 stdlib 22 库 → per-member 布局 + 功能正确（**非整包 byte-identical**：整包差异 pre-existing，gate 只验 --emit-zbc + 功能；见 Milestone 2 结论）
- [x] 4.4 docs/design/compiler/self-hosting.md 同步（z42c driver 自有 build --workspace M1/M2 段）

## 备注
- z42c 受限子集：无泛型字段 → 平行数组（name[]/deps[][]/count）；无 enum → 颜色用 int
- M1 输出布局 flat（管线 Z42_LIBS 用 flat）；**M2 per-member 布局已落地**（PathTemplate ${project_name} 补齐 + DepScan 多目录 first-wins 去重）。
- 4.3 措辞修正：原写"22 库 == C# 输出 逐 zpkg byte compare"不可达（整包 stdlib zpkg 对 C# 有 ~1-3% pre-existing 差异）；实际验证 = per-member 布局正确产出 + z42c-built stdlib 功能正确（mirror S2.1）。
