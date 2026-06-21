# Proposal: z42c-build-workspace

## Why

z42c 替换 C# 编译器的**唯一硬前置**：z42c.driver 的 `build` 只编单工程，缺
`build --workspace`（C# 用它编 stdlib 22 库 + z42c 自身 7 包，自动 dep 拓扑序）。
管线只用 `--workspace`（全建），`-p`/`--exclude`/default-members 不在关键路径。
顺带补 `clean`（User 要求保留）。

## What Changes

z42c.driver 加 `build --workspace`：
1. **成员发现**：展开 `[workspace] members` glob（`*` → 各子目录的 `*.z42.toml`），
   读每个成员的 `[project].name` + `[dependencies]`（兄弟依赖）。
2. **拓扑排序**：成员 dep 图（仅 workspace 内边）→ 找环（报错）→ 拓扑序。
   平行数组实现（z42 受限子集无泛型字段）。镜像 C# `MemberDependencyGraph`。
3. **build loop**：拓扑序逐成员调 `_build`，产物落共享 flat dist（= `Z42_LIBS`），
   deps-first 保证后续成员解析到兄弟 zpkg。上游失败 → 下游 blocked。
   镜像 C# `WorkspaceBuildOrchestrator`。
4. **`clean`**：删 workspace/工程产物目录。

**正确性门**：`./xtask test compiler-z42` byte-identical（z42c `build --workspace`
编 z42c 7 包 == C# 输出）+ z42c build stdlib 22 库 == C# 输出。

## Scope（允许改动的文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/z42c/z42c.driver/src/Main.z42` | MODIFY | `build --workspace` 分支 + `clean` 命令 + `_build` libsDir 参数化 |
| `src/z42c/z42c.pipeline/src/WorkspaceBuild.z42` | NEW | 成员发现 + dep 图 + 拓扑 + build loop（端口 C# orchestrator+graph）|
| `src/z42c/z42c.pipeline/tests/workspace_topo/` | NEW | 拓扑/环检测单测 |
| `docs/design/compiler/self-hosting.md` | MODIFY | workspace build 端口记录 |

**只读引用**：
- `src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs` / `MemberDependencyGraph.cs` — C# 参考
- `src/z42c/z42c.project/src/ManifestLoader.z42`（`LoadWorkspace`/`WorkspaceManifest` 已有）
- `src/z42c/z42c.project/src/ProjectModel.z42`（`ProjectManifest.Name`/`Deps`）

## Out of Scope（替换后按需补，不阻塞）
- `-p` / `--exclude` / default-members 选择（管线不用；可后补对齐 C#）
- `check` / `run` / `publish`(driver) / query(info/metadata/tree/lint) / `new` / disasm/golden
- 拓扑**分层**并行（C# 也串行；分层只是元数据）

## Open Questions
- [ ] 输出布局：flat-only（最简）vs per-member dist + flat 聚合（C# parity）—— 实施时按 downstream 实际需求定（管线 Z42_LIBS 用 flat，倾向 flat 足够）
