# Tasks: consolidate-platform-into-workload

> 状态：🟢 已归档 2026-06-19（S0–S5 全完成；B 子系统设计转 workload-subsystem-future 归档） | 创建：2026-06-17
> 类型：refactor（结构整合）+ docs。S0 docs-only；物理迁移各独立 change。

## 进度（2026-06-17）

| 步 | change | 状态 |
|----|--------|------|
| S0 设计落地 + 冲突修复 | consolidate-platform-into-workload | ✅ 12015219 |
| S1 Tier2 embed→host-api | ~~migrate-tier2-to-workload~~ | ✅ 87e7cce4 |
| S2 apphost（重构为 apphost-as-config）| ~~add-desktop-export~~ | ✅ 77a5acba（apphost=`[platform.desktop]` 配置，非命令；非"desktop workload 脚手架"）|
| S3 facade→workload/platforms | ~~migrate-facades-to-workload~~ | ✅ 45da148a（后经 2026-06-18 refactor 扁平化为 `workload/<plat>/platform/`，见 workload/README.md）|
| S5 host/ 移除 | ~~remove-host-toplevel~~ | ✅ 本 change |
| **B 子系统**（B1 命令发现 / B2 打包+auto-runtime / B4 测试改 workload 驱动 / B5 export 生命周期）| build-workload-subsystem | 📋 立项 29380849，待实施 |

> S4（测试改 workload 驱动）并入 B4（依赖 workload 脚手架）。下方原始大纲保留作历史。

## S0 — 设计落地 + 冲突修复（docs-only，本 change）

- [x] 0.1 修冲突：launcher-command-dispatch.md apphost 移出 Core → Workload
- [x] 0.2 修冲突：runtime-workload-distribution.md desktop 入 workloads + Decision 9
- [x] 0.3 proposal.md（why + 目标组件归属 + Scope）
- [x] 0.4 design.md（组件归属图 + 5 决策 + 门控模型 + 迁移路线 S1–S5）
- [x] 0.5 tasks.md（本文件）
- [ ] 0.6 workload/README.md charter 改为目标结构
- [ ] 0.7 ACTIVE.md 登记 change 名
- [ ] 0.8 验证：文档链接自检；冲突修复前后一致性
- [ ] 0.9 COMMIT + push（docs-only）

## S1–S5 — 物理迁移大纲（各自开独立 change，开工前查 ACTIVE.md 占锁）

### S1 · Tier2 embed → workload/host-api（锁：runtime + toolchain）
- [ ] `host/embed` 迁 `workload/host-api`；3 个 facade crate path-dep 改向
- [ ] 简化 runtime `src/host/` 结构（不增职责）
- [ ] GREEN：cargo + 各平台 build 冒烟

### S2 · apphost → desktop workload + workload 脚手架骨架（锁：toolchain）
- [ ] `launcher/core/apphost.z42` 迁 desktop workload
- [ ] `z42 apphost build` 命令面迁移为 desktop workload publish
- [ ] workload 目录脚手架引擎骨架

### S3 · facade + 模板 → workload；wasm demo 归位（锁：toolchain）
- [ ] `host/platforms/*` facade + 工程模板 → `workload/{facades,templates}`
- [ ] wasm `demo/node`→conformance、`demo/web`→browser-example（直接落终态，撤销旧 P1 微清理）
- [ ] 9 个 xtask 路径字面量同步
- [ ] embedding.md §package 布局 + export-lifecycle line 90 骨架来源同步

### S4 · R1–R7 改 workload 驱动（锁：toolchain，+runtime 验证）
- [ ] R1–R7 测试改由 workload 生成/驱动，成 workload 自身测试用例
- [ ] 删 `host/platforms/*/tests`；CI 路径同步；留极简 smoke 兜底
- [ ] GREEN：4 平台 test 经 workload 跑通

### S5 · host/ 移除 + 文档收口（锁：docs）
- [ ] `src/toolchain/host/` 顶层删除
- [ ] embedding.md / platforms README / 分发文档收口到新结构
- [ ] 全量链接自检

## 备注

- packager 不在本 change 及 S1–S5 范围（用户暂缓）。
- 每步迁移按 philosophy「不为破坏性顾虑而牺牲最佳方案」——调用点更新是同一迭代的机械工作，GREEN 收尾。
