# consolidate-platform-into-workload — proposal

> 状态：**S0 设计落地（docs-only）**（2026-06-17）。本 change 只产设计 + 迁移路线，不动代码；物理迁移走后续 S1–S5 各自独立 change。
> 子系统锁：`docs`（不上锁）。S1–S5 各自占 `runtime`/`toolchain` 锁。

## Why

`src/toolchain/host/` 把**平台无关核心**与**平台相关工程**缠在一起，职责蔓延、结构混乱：
- 每个 `platforms/<p>/` 同时塞了：SDK facade（要 ship 的库）、binding、打包配置、一致性测试（内部）、demo（wasm 还混了"测试伪装成 demo"）。
- Tier2 `embed` crate、各平台 facade、桌面 apphost 散落在 `host/` 与 `launcher/`。

而分发模型（[runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md)）与生命周期（[platform-export-lifecycle.md](../../../design/toolchain/platform-export-lifecycle.md)）**已经设计完备**：runtime（host install）/ workload（按需 `z42 workload install`）二分、`z42 build/export/publish/test`、单一 `[Test]` 套件两面跑。缺的不是设计，是**把代码结构落到这套分工上**。

不做会怎样：`host/` 继续是"平台相关与无关混居"的债务区，后续真要支持全平台应用开发（workload 落地）时每加一个平台都要在错误的结构上打补丁。

## What Changes（目标结构，逐步迁移）

把"平台相关的一切"统一收进 **workload**，让 **runtime 留最小平台无关核心**，**launcher 留 SDK**：

| 组件 | 职责（目标） | 现状位置 → 目标位置 |
|------|------|------|
| **runtime** | VM + Tier1 C ABI + 头文件 + per-RID 原始库（简化优化现有结构）| 已在 `src/runtime/`（`src/host/` + `include/`），保持 |
| **workload** | Tier2 人因层 + 各平台 facade + 工程模板 + 导出/编译下载 + apphost（desktop）+ R1–R7 测试 | ← `host/embed`、`host/platforms/*`、`launcher/core/apphost.z42` |
| **launcher** | `z42` CLI core（install/build/run/list/...），属 SDK | 保持 `src/toolchain/launcher/`（apphost.z42 迁出）|
| **packager** | （SDK installer 方向，**本 change 不处理**，后议）| 不动 |
| **host/** | **解散**：embed→workload、platforms→workload；顶层移除 | 删除 |

**workload 门控（与既有分发/生命周期设计一致）**：默认 `z42 build`/`run` 用 host runtime、**零 workload**；`z42 publish`（含 desktop apphost）/ `z42 export` 才下载对应平台 workload（desktop/ios/android/wasm 对称）。

## 前置：已解决的两处规范冲突（本 change 已修）

1. [launcher-command-dispatch.md](../../../design/toolchain/launcher-command-dispatch.md)：apphost 从 Core 移到 Workload（desktop publish 产物）。
2. [runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md)：`workloads` 加 `desktop`（仅 publish/export 维度，不含 runtime）；Decisions +1 行。

## Scope（S0，docs-only）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/design/toolchain/launcher-command-dispatch.md` | MODIFY | apphost 移出 Core（冲突修复，已做）|
| `docs/design/toolchain/runtime-workload-distribution.md` | MODIFY | desktop 入 workloads + Decision 9（冲突修复，已做）|
| `docs/spec/changes/consolidate-platform-into-workload/proposal.md` | NEW | 本文件 |
| `docs/spec/changes/consolidate-platform-into-workload/design.md` | NEW | 组件归属 + 迁移路线 S1–S5 |
| `docs/spec/changes/consolidate-platform-into-workload/tasks.md` | NEW | S0 任务 + S1–S5 大纲 |
| `src/toolchain/workload/README.md` | MODIFY | charter 改为本目标结构 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记 change 名 |

**只读引用**：`src/toolchain/host/**`（理解现状）、`docs/design/runtime/embedding.md`（package 布局）、`platform-export-lifecycle.md`（生命周期）。

## Out of Scope

- 任何**代码/文件物理迁移**（归 S1–S5，各自独立 change）。
- **packager** 重定位（用户决定：原为 SDK installer 方向，暂不处理）。
- 分发/生命周期模型的**重新设计**（已有文档为准，本 change 只对齐代码结构）。

## Open Questions

- [ ] packager 与 desktop workload publish 的边界（用户已暂缓，后议）。
- [ ] Tier2 `embed` 迁入 workload 后的 crate 命名 / workspace 归属（S1 细化）。
- [ ] `z42 export` 既有命令（add-export-command 已落地）与 workload 模板的衔接（S3 细化）。
