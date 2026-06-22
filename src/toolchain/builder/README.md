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
- **编译本身** —— 调 `z42c` 完成（Compile 相位里调用编译器，本模块只编排）。
- **平台专属实现** —— 住各 workload 的 `*.workload.zpkg`（`: WorkloadBase` 子类）。
- **管线接口/相位流程定义** —— 住 [`src/libraries/z42.build/`](../../libraries/z42.build/)
  （`Pipeline` / `IPipelineContext` / `WorkloadBase` / `BuildHooks`）。本模块是**驱动方**。

> **取代原 `packager/` 占位**：旧 packager 设想的「把 z42 程序 + 运行时打成可分发件」
> 只是本管线尾部 `Assets` / `Package` 两个相位的一部分；构建编排是其超集，故 packager
> 占位并入本目录，不再单列。

## 计划模块（`core/`，尚未实现）

| 模块 | 职责 |
|------|------|
| 命令入口 | 解析 launcher 透传的 `build`/`publish`/`export`/`run --rid`/`test` 参数 |
| driver 装配 | 从 `z42.build` + 选中 workload + 项目 `build/` 自定义脚本组装可跑的管线 driver |
| `IPipelineContext` 注入 | 提供 SDK 侧的上下文实现（受限 fs / exec / 产物登记 / 平台原语）|

> **`IPipelineContext` 具体实现的最终归属待定**：取决于 driver 装配方式
> （生成源码+z42c 静态编译 → impl 须为可 import 的库，宜住 `src/libraries/`；
> 固定通用 driver → impl 可住本目录）。落地 spec 时钉死。

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
