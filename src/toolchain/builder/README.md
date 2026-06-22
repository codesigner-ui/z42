# toolchain/builder — z42 构建编排器（`z42b`）

## 职责

z42 项目「编译 → 发布」**全流程的构建编排器**：读 `z42.toml` / `--rid` →
装配并驱动 [`z42.build`](../../libraries/z42.build/) 管线，逐相位调度执行
（Resolve → Compile → Trim → Assets → Configure → GenerateProject → NativeBuild → Package）。
编译为 `z42b.zpkg`（Exe-mode），由 launcher 命令分发调用
（`z42 build` / `publish` / `export` / `run --rid` / `test`）。

类比关系（沿用 launcher 的「z42 源 → zpkg → apphost」模式）：

```
src/toolchain/builder/core/*.z42  →  z42b.zpkg  →  apphost z42b
（对照 launcher/core/*.z42 → launcher.zpkg → z42）
```

**不做**：
- **编译本身** —— 经 `z42.build` 的 `ICompiler` 接口**在进程内**调编译器库（z42c）。
  与独立 `z42c.driver` CLI **引用同一份实现，不 fork z42c 子进程**；本模块只编排 + 注入。
- **平台专属实现** —— 住各 workload 的 `*.workload.zpkg`（`: WorkloadBase` 子类）。
- **管线接口/相位流程定义** —— 住 [`src/libraries/z42.build/`](../../libraries/z42.build/)
  （`Pipeline` / `IPipelineContext` / `ICompiler` / `WorkloadBase` / `BuildHooks`）。本模块是**驱动方**。

> **取代原 `packager/` 占位**：旧 packager 设想的「把 z42 程序 + 运行时打成可分发件」
> 只是本管线尾部 `Assets` / `Package` 两个相位的一部分；构建编排是其超集，故 packager
> 占位并入本目录，不再单列。

## 核心文件（`core/`，PARKED 骨架）

| 文件 | 职责 |
|------|------|
| `core/builder.z42` | z42b 命令入口 + 编排骨架：解析动词/toml/--rid → 构造 `Pipeline`（注入 `ICompiler` + workload + hooks）→ 跑。展示**标准路径**（进程内组合，零子进程/零代码生成）与**自定义路径**（项目带 `build/` → 生成一次性 driver）两条骨架 |

## 计划模块（实现期补全）

| 模块 | 职责 |
|------|------|
| driver 装配 | 项目带自定义 `build/` 时，组装一次性 driver 源码（链 `z42.build` + workload + 项目 `build/`），用**同一 `ICompiler`** 编译后运行 |
| 共享编译实现适配 | `_hostCompiler()` 返回编译器库（z42c）的 `ICompiler` 实现 —— 与 `z42c.driver` 同一份 |

> **`IPipelineContext` 实现归属（2026-06-23 决策）**：暂置 `z42.build` 库
> （[`PipelineContext.z42`](../../libraries/z42.build/src/PipelineContext.z42)），编排器 import 它构造 ctx。
> in-process 编译让**标准路径无需生成 driver**（直接进程内组合 Pipeline 跑），仅项目带自定义
> `build/` 的自定义路径才落 driver 生成。
>
> **计划重构**：`ICompiler` 等编译接口后续抽到中立微库，z42c 与 z42b 同依赖该微库——面向接口，
> 「改成直接调 z42c」只换实现不动调用方。落地写 `build-orchestrator.md` 时入 Deferred。

## 依赖关系

- 依赖 [`src/libraries/z42.build/`](../../libraries/z42.build/)（管线框架接口）、
  [`src/libraries/z42.project/`](../../libraries/z42.project/)（`z42.toml` 模型）。
- 调用 `z42c`（编译）、各 workload（平台尾相位）；经 `extern` 调 VM native 原语
  （Sign / Archive / Hash / Download / ProbeVersion，住 `runtime`）。
- 被 launcher 命令分发调用（见 [`docs/design/toolchain/launcher-command-dispatch.md`](../../../docs/design/toolchain/launcher-command-dispatch.md)）。

## 状态

🔴 **占位 / 未接编译**。当前仅目录骨架 + 本 README，**未登记 workspace / xtask / CI**，
不影响任何现有构建。

落地走 spec-first（架构性变更），设计文档 `docs/design/toolchain/build-orchestrator.md`（待建）。
**前置**：replace-csharp S5 完成（z42c 成生产编译器、`toolchain` 子系统解锁）。
推进计划见 `docs/roadmap.md`。
