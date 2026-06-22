# Proposal: 0.4.x 重定位为「质量与性能线」（4 流并行 + G 前置流）

## Why

0.3.x「自举线」即将收尾（编译器 7 子系统用 z42 重写到 byte-identical）。原 roadmap 的 0.4.x 被规划为线性「填 stdlib 包」（0.4.0 core → 0.4.1 exceptions → … → 0.4.8 docgen）。但现实是：**24 个 stdlib 包已 ship**（含 net/crypto/compression/yaml/numerics），0.4.x 的真实价值不再是"建 stdlib"，而是：

1. 把 0.3.x 攒下的、**已设计未实现的高 ROI 性能杠杆**兑现（VM + 脚本）；
2. 把 benchmark 从"骨架 + informational 提示"升级为**z42 原生工具（`z42.bench` / `z42c bench`）+ 硬门禁**；
3. 补齐**小而自包含的语法缺口**（`params` / `init` / 索引器 / 命名实参 / `partial`）；
4. 把已有 stdlib 从"能用"打磨到"好用"（模块划分审计 + JSON 反序列化 + CLI 对标 dotnet/python）。

不做的代价：0.4.x 继续按"填包"推进 → 与已落地包重复，且把真正欠的"性能兑现 + 工具 GA + 语法补齐"继续往后拖。

## What Changes

- **重构 0.4.x 为 4 主线并行**（沿用 0.3.x 子系统互斥锁模型）：
  - **P（perf）** `runtime`+`stdlib`：兑现 jit.md / ir-specialization.md / tiered-execution.md 已设计的杠杆（JIT 算术拆箱、inline cache + quickening、intrinsic 折叠、非原子 refcount、devirt、stdlib 脚本 perf 三轮）。
  - **B（bench）** `toolchain`+`stdlib`：抽独立 `z42.bench` 包 + `z42c bench` GA + e2e 硬门禁 + PR 自动 diff 评论 + perf 库 baseline 铺面。
  - **S（syntax）** `compiler`+`z42c`：`params` / `init` + 表达式体属性 / 索引器 / 命名实参 / `partial` class。
  - **L（lib）** `stdlib`：模块划分审计 + JSON（`JsonReader` 流式 + `JsonSerializer` 非泛型 + `Deserialize<T>` 泛型 serde）+ CLI（值校验 + 全局 flag + shell 补全）+ `z42-doc`。
- **新增 G（泛型前置）隐藏流** `compiler`+`runtime`：运行期泛型实例化 + 泛型反射三件套（泛型方法 Invoke / `MakeGenericType` / `Activator.CreateInstance<T>`）。**由 L 流的 `Deserialize<T>` 招牌依赖**（User 2026-06-23 裁决"硬上完整泛型 serde"）。
- **从后续版本移除被提前的工作**（避免重复规划）：
  - 原 0.4.7「z42.bench v1 + z42c bench」→ 并入 0.4.x B 流前段（0.4.0）。
  - 原 0.5.x「反射泛型扩展（泛型方法 Invoke + MakeGenericType + Activator.CreateInstance<T>）」→ 上移到 0.4.x G 流。0.5.x 反射条目清空，主题回到「泛型完整 + Trait + LSP + Interop 2a」。
  - 原 0.4.0–0.4.6 线性 stdlib「填包」表整体作废（包已存在），改为 L 流的"打磨"。
- **同步更新 roadmap.md**：0.4.x 段、0.5.x 段、跨版本依赖图（反射链）、Feature→Version 映射（§15 反射行）、横向工作流（z42c bench 启用版本）、GREEN 标准演进、Toolchain 矩阵。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/spec/changes/plan-0.4.x-four-streams/proposal.md` | NEW | 本提案 |
| `docs/spec/changes/plan-0.4.x-four-streams/design.md` | NEW | 4 流编排 + G 流连锁 + 锁协调决策 |
| `docs/spec/changes/plan-0.4.x-four-streams/specs/roadmap-0.4.x/spec.md` | NEW | 0.4.x 退出标准（可验证场景）|
| `docs/spec/changes/plan-0.4.x-four-streams/tasks.md` | NEW | 落盘任务清单 |
| `docs/roadmap.md` | MODIFY | 0.4.x 段重写 + 0.5.x 反射收窄 + 依赖图 + Feature 映射 + 横向工作流 + GREEN 演进 + Toolchain 矩阵 |

**只读引用**：

- `docs/design/runtime/{jit,ir-specialization,tiered-execution,gc}.md` — perf 杠杆出处
- `docs/design/stdlib/{json,cli,organization,roadmap}.md` — L 流现状
- `docs/design/language/{reflection,properties,language-overview}.md` — S 流 + G 流现状
- `bench/README.md` — bench 基础设施现状

## Out of Scope

- **不实施任何代码**：本变更只产出 0.4.x 规划文档 + roadmap 更新；各子版本的 spec 在 0.4.x 启动时逐个开。
- **不动 0.3.x in-flight**：replace-csharp / remove-dotnet-from-builds 等仍按 0.3.x 推进。
- **不提前 C 档大 L3 语法**：`let`/不可变（0.6）、运算符重载/扩展方法（0.7）、`match`/ADT（0.7）、async（0.8）不进 0.4.x。
- **不把 philosophy §9 五指标达标承诺提前**：仍在 0.10.x。0.4.x perf = 兑现高 ROI 杠杆 + 建立防退化门，非达标承诺。
- **OSR/deopt 框架不进 0.4.x**：P 流只做不依赖 deopt 的杠杆；OSR/deopt（JIT 分层 + hot-reload 共用地基）留 0.5.x。

## Open Questions

> **2026-06-23 User 裁决"按建议"，全部已定。**

- [x] Q-A：**认可** `partial` class 作为正式代码组织手段——在 z42 唯一动机即 200 行硬限下拆自举编译器大类（无表达力增益）；放 0.4.4，优先级最低。
- [x] Q-B：**G 流独立先行于 S 流**（serde 招牌依赖 G）；二者串行争 `compiler`/`z42c` 锁，G 先 S 随。
- [x] Q-C：**采纳 JSON 两步交付**——0.4.2 先落非泛型 `JsonSerializer`（依赖已落地 0.3.12 Invoke）保产物，G 流就绪后 0.4.3 上 `Deserialize<T>` 泛型版。
- [x] Q-D：**接受 L3 提前例外**——完整泛型 serde 把 0.5.x L3-R 反射泛型扩展提前到 0.4.x G 流，显式登记为 0.4.x 招牌前置；0.5.x 反射条目相应清空。
