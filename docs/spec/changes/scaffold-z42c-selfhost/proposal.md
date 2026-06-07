# Proposal: B0 — 搭建 z42c 自举编译器骨架（`src/z42c/`）

> 状态：📋 草案（待 User 阶段 6.5 确认）｜类型：架构（编译器自举线 B 主线起点）｜责任人：User + Claude
>
> 父规划：[`plan-0.3.x-three-streams/proposal.md`](../plan-0.3.x-three-streams/proposal.md) 0.3.1 行（B0）。

## Why

0.3.x 自举线 **B 主线（编译器全自举）** 的第一块落脚点。按父规划，0.3.1 = **B0 架构 spec + 建 7 子包骨架 + xtask `build/test compiler-z42`**。

本变更**只立骨架与构建管线**，不写任何真实编译器逻辑（Lexer / Parser / Semantics / IR / Pipeline 是 0.3.3 起的后续独立 spec）。不做则 B 主线无 workspace 可挂，后续 core/syntax 等 spec 无处落地。

**目录命名裁决（2026-06-07）**：源码根目录用 **`src/z42c/`**（覆盖父规划 2026-06-06 的 `src/z42.compiler/`），保留 **7 子包 1:1 镜像** C# 项目，产物命名 **`z42c.<sub>.zpkg`**。本变更同步把 roadmap + 父 proposal 的旧目录名改正，消解冲突、确立单一真相来源。

## What Changes

- 新建 `src/z42c/` **独立 workspace**（与 `src/libraries/` stdlib workspace 解耦），7 个子包占位：`core / syntax / project / driver / semantics / ir / pipeline`，**包间依赖图镜像 C# 项目引用**。
- 每子包：manifest（`kind = lib`，driver 为 `exe`）+ 一个可编译的**占位类型** + 子目录 README。driver = `z42c` 入口别名，`Main()` 仅打印 banner（**无桥接**：不实现任何命令，绝不 fallback 到 dotnet z42c.dll）。
- xtask 扩展：`build compiler-z42`（= z42c workspace 编译 7 子包）/ `test compiler-z42`（= 编译 + 断言 7 zpkg 产出的 smoke；逐字节对账留待 0.3.3 有真实产物时）；`build all` 级联末尾追加 compiler-z42。
- 新建 durable 架构文档 `docs/design/compiler/self-hosting.md`：受限写法约定 / 目录布局 / 依赖图 / byte-identical 策略 / 无桥接 CLI parity / perf gate / 1.0 切换路径 / dogfood 反馈循环。
- 同步文档：roadmap + 父 proposal 把 `src/z42.compiler/` → `src/z42c/`、`z42.compiler.<sub>.zpkg` → `z42c.<sub>.zpkg`。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42.workspace.toml` | NEW | 独立 workspace（`members=["*"]` + 拓扑序 default-members + output 到 `artifacts/build/z42c/`）|
| `src/z42c/README.md` | NEW | 顶层目录 README（职责 / 子包表 / 依赖图 / 构建入口）|
| `src/z42c/core/z42c.core.z42.toml` | NEW | core 清单（lib，无 z42c 兄弟依赖）|
| `src/z42c/core/src/CoreSkeleton.z42` | NEW | 占位类型 `Z42.Core` |
| `src/z42c/core/README.md` | NEW | 子包 README |
| `src/z42c/ir/z42c.ir.z42.toml` | NEW | ir 清单（lib，无兄弟依赖，镜像 C# z42.IR）|
| `src/z42c/ir/src/IrSkeleton.z42` | NEW | 占位类型 `Z42.IR` |
| `src/z42c/ir/README.md` | NEW | 子包 README |
| `src/z42c/syntax/z42c.syntax.z42.toml` | NEW | syntax 清单（lib，dep core）|
| `src/z42c/syntax/src/SyntaxSkeleton.z42` | NEW | 占位类型 `Z42.Syntax` |
| `src/z42c/syntax/README.md` | NEW | 子包 README |
| `src/z42c/project/z42c.project.z42.toml` | NEW | project 清单（lib，dep ir）|
| `src/z42c/project/src/ProjectSkeleton.z42` | NEW | 占位类型 `Z42.Project` |
| `src/z42c/project/README.md` | NEW | 子包 README |
| `src/z42c/semantics/z42c.semantics.z42.toml` | NEW | semantics 清单（lib，dep core+syntax+ir）|
| `src/z42c/semantics/src/SemanticsSkeleton.z42` | NEW | 占位类型 `Z42.Semantics` |
| `src/z42c/semantics/README.md` | NEW | 子包 README |
| `src/z42c/pipeline/z42c.pipeline.z42.toml` | NEW | pipeline 清单（lib，dep core+syntax+semantics+ir+project）|
| `src/z42c/pipeline/src/PipelineSkeleton.z42` | NEW | 占位类型 `Z42.Pipeline` |
| `src/z42c/pipeline/README.md` | NEW | 子包 README |
| `src/z42c/driver/z42c.driver.z42.toml` | NEW | driver 清单（exe，pack=true，dep pipeline+ir+core）|
| `src/z42c/driver/src/Main.z42` | NEW | `Main()` banner（无桥接）|
| `src/z42c/driver/README.md` | NEW | 子包 README |
| `src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs` | MODIFY | `BuildLibsDirs` 加 `workspaceLibDirs` 形参（当前 workspace 成员 dist 目录，按规范化 full-path 去重 + 排序追加）；`BuildTarget` 透传 |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | MODIFY | `RunResolved` 加 `workspaceLibDirs` 形参，透传 BuildTarget |
| `src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs` | MODIFY | `Build` 收集各成员 `EffectiveDistDir`（排序去重）→ 透传 `CompileMember`（Func 增第 3 形参）|
| `src/compiler/z42.Tests/WorkspaceBuildOrchestratorTests.cs` | MODIFY | mock Func 改 3 参 + 新增 sibling dist 透传单测 |
| `src/compiler/z42.Tests/WorkspaceFullExampleTests.cs` | MODIFY | mock Func 改 3 参 |
| `docs/design/compiler/compiler-architecture.md` | MODIFY | 记 workspace 兄弟解析机制（实现原理） |
| `scripts/xtask.z42` | MODIFY | `_build`/`_test` 路由 + help + `build all` 级联 + unknown-target 提示 |
| `scripts/xtask_compiler_z42.z42` | NEW | `_buildCompilerZ42` / `_testCompilerZ42`（mirror xtask_stdlib，针对 src/z42c）|
| `scripts/xtask.z42.toml` | MODIFY | `[sources].include` 加 xtask_compiler_z42.z42 |
| `docs/design/compiler/self-hosting.md` | NEW | 自举 durable 架构文档 |
| `docs/design/compiler/README.md` | MODIFY | 核心文件表加 self-hosting.md |
| `docs/roadmap.md` | MODIFY | `src/z42.compiler/`→`src/z42c/` + B0 行状态 + M10 进度 |
| `docs/spec/changes/plan-0.3.x-three-streams/proposal.md` | MODIFY | `src/z42.compiler/`→`src/z42c/` + zpkg 命名 |

**只读引用**（理解上下文，不修改）：

- `src/compiler/README.md` + 各子项目 `README.md` — 7 项目职责 / 依赖图镜像源
- `src/libraries/z42.workspace.toml` + `src/libraries/z42.core/z42.core.z42.toml` — workspace + 清单格式参照
- `scripts/xtask_stdlib.z42`（`_buildStdlibCore`）— workspace 编译调用参照
- `scripts/xtask_common.z42` — `_exec` / `_root` / 路径 helper

## Out of Scope

- 真实 Lexer/Parser/Semantics/IR/Pipeline 逻辑（→ 0.3.3 起后续 spec）
- byte-identical gate **激活**（→ 0.3.3 有真实产物后；本变更只留 smoke 占位）
- compile-perf gate（→ 0.3.10）
- A 主线 stdlib 重组（独立线；本骨架占位不依赖重组）
- 任何**新语言特性 / 新语法**（占位代码只用已落地子集；若后续 dogfood 需要，单独 spec 讨论）

## Open Questions（已给默认值，阶段 6.5 确认）

- [ ] **byte-identical gate 频率**：默认 **per-PR**（CI 文本/字节 diff），激活于 0.3.3；本变更不实装。
- [ ] **受限写法 AST/IR 形态**：默认 **class 继承 + 抽象 `Visitor` 基类**（不用 record+match），具体节点定义留 syntax spec（参考 [[D-11]] introduce-bound-visitor）。本变更只在 self-hosting.md 固化约定。
- [ ] **z42c 内部命名空间**：默认**镜像 C#** `Z42.Core` / `Z42.Syntax` / `Z42.IR` / `Z42.Project` / `Z42.Semantics` / `Z42.Pipeline` / `Z42.Driver`。
