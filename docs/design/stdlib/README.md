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
| [`api-guidelines.md`](api-guidelines.md) | **接口面设计准则**：Source×Operation 漏斗（避免 m×n）+ 正交轴收敛 + 便利糖薄委托 |
| [`organization.md`](organization.md) | 包边界 + 现状 5 包（z42.core / z42.io / z42.collections 等）|
| [`roadmap.md`](roadmap.md) | P0–P3 排期：time / fs / threading / encoding / net 等缺失包 |

> **注**：`organization.md` 与 `roadmap.md` 是**过程文档**，不构成硬约束，方案设计期可灵活引用调整（参见 memory `feedback_stdlib_docs_not_final`）。

## 层级命名（避免混淆）

stdlib 同时使用两套层级，**它们不是同义词**：

- **Tier 1 / 2 / 3**（实现层级，见 [`overview.md`](overview.md)）：VM Intrinsic / Platform HAL / Script BCL —— 描述 stdlib 的物理实现位置
- **L0 / L1 / L2 / L3**（包依赖层级，见 [`organization.md`](organization.md)）：z42.core / z42.collections.text.math / z42.io.threading / z42.net.linq.json —— 描述包之间的依赖方向（上层依赖下层，禁止反向）

两套独立。一个 z42.core 类型可以是 Tier 1 (VM intrinsic) 实现 + L0 (依赖底)；一个 z42.linq 包是 Tier 3 (Script BCL) 实现 + L3 (依赖最上)。

## 入口点

- 新写 stdlib 代码：[`overview.md`](overview.md) → [`organization.md`](organization.md) 找包归属
- 立项新包：[`roadmap.md`](roadmap.md) 检查是否已排期

## 依赖关系

- 上游：[`../language/`](../language/)（语言特性决定 stdlib 能用什么）
- 下游：`src/libraries/`（实际 .z42 源码）
