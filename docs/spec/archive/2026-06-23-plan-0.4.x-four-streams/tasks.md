# Tasks: 0.4.x 重定位为四流并行规划

> 状态：🟢 已完成 | 创建：2026-06-23 | 完成：2026-06-23
> 类型：docs（规划）—— 不上子系统锁

## 进度概览
- [x] 阶段 1: 规划文档（proposal / design / spec）
- [x] 阶段 2: roadmap.md 更新（重写 + 移除被提前项）
- [x] 阶段 3: 一致性验证 + 归档（Open Questions 已裁决"按建议"）

## 阶段 1: 规划文档
- [x] 1.1 proposal.md（Why / 4 流 + G 流 / 移除项 / Open Questions）
- [x] 1.2 design.md（5 项决策 + 锁协调）
- [x] 1.3 specs/roadmap-0.4.x/spec.md（五流退出标准 + 引用一致性）

## 阶段 2: roadmap.md 更新
- [x] 2.1 重写 0.4.x 段（线性填包表 → 4 流并行表 + 退出标准 + G 前置流）
- [x] 2.2 收窄 0.5.x 段（移除反射泛型扩展 → OSR/deopt；主题泛型完整+Trait+LSP+Interop 2a）
- [x] 2.3 更新跨版本依赖图（反射链：泛型 Invoke/MakeGenericType 从 0.5.x → 0.4.x G 流）
- [x] 2.4 更新 Feature→Version 映射 §15 反射行
- [x] 2.5 更新横向工作流表（z42c bench 启用版本 0.4.7 → 0.4.x；z42c test → 0.3.13）
- [x] 2.6 更新 GREEN 标准演进表（0.4.6/0.4.7/0.4.8 行 → 新 0.4.x 流退出项）
- [x] 2.7 更新 Toolchain 矩阵（z42-doc 起始版本 0.4.8 → 0.4.x L 流）
- [x] 2.8 grep 验证无悬挂引用（0.4.7/0.4.8/反射泛型扩展残留均为有意说明）

## 阶段 3: 验证 + 归档
- [x] 3.1 grep 检查无悬挂引用（残留均为有意"作废/移除项"说明）
- [x] 3.2 Open Questions Q-A/Q-B/Q-C/Q-D 由 User 裁决"按建议"并回填
- [x] 3.3 归档 → docs/spec/archive/2026-06-23-plan-0.4.x-four-streams/
- [x] 3.4 commit（.claude/ + docs/spec/ 纳入；按 feedback_commit_no_push 默认不 push）

## 阶段 4: P 流细化（2026-06-23 补充）
- [x] 4.1 编译器侧 + VM 侧框架探查（Explore ×2）
- [x] 4.2 修正 roadmap P 列（标注已落地 IC/I64 特化；P 拆 Pv/Pc 两侧）
- [x] 4.3 design.md 新增「P 流细化」段（已落地基线 + Pv1–6 + Pc1–5 + 耦合约束）

## 备注
- JSON「硬上完整泛型 serde」(User 2026-06-23) 把 0.5.x L3-R 反射泛型扩展提前到 0.4.x G 流 → 违反"不为单点提前半个 L3"，作为显式例外登记（Q-D）。
- partial class 唯一动机=拆自举编译器大类（200 行硬限替代解法），表达力无增益（Q-A）。
