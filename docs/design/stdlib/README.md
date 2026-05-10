# design/stdlib/

z42 标准库三层架构、包划分规则、缺失包排期。

## 职责

- 描述 stdlib 三层架构（intrinsic / HAL / script BCL）+ Script-First 规则
- 描述当前 5 个包的边界与现状
- 排期 P0–P3 缺失包

## 核心文件

| 文件 | 职责 |
|------|------|
| [`overview.md`](overview.md) | 三层架构 + Script-First 规则 + Per-Package extern 预算（**架构权威**）|
| [`organization.md`](organization.md) | 包边界 + 现状 5 包（z42.core / z42.io / z42.collections 等）|
| [`roadmap.md`](roadmap.md) | P0–P3 排期：time / fs / threading / encoding / net 等缺失包 |

> **注**：`organization.md` 与 `roadmap.md` 是**过程文档**，不构成硬约束，方案设计期可灵活引用调整（参见 memory `feedback_stdlib_docs_not_final`）。

## 层级命名（统一）

`L0 / L1 / L2 / L3` 在所有 stdlib 文档中保持同义；不再混用 "Layer 1/2/3"。

## 入口点

- 新写 stdlib 代码：[`overview.md`](overview.md) → [`organization.md`](organization.md) 找包归属
- 立项新包：[`roadmap.md`](roadmap.md) 检查是否已排期

## 依赖关系

- 上游：[`../language/`](../language/)（语言特性决定 stdlib 能用什么）
- 下游：`src/libraries/`（实际 .z42 源码）
