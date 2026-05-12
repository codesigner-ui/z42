# Tasks: 定义平台 facade 测试契约

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：lang-adjacent docs（无代码，纯规范）

## 进度概览

- [ ] 阶段 1: spec 文档
- [ ] 阶段 2: design doc 同步（embedding.md + platforms/README.md）
- [ ] 阶段 3: 归档

## 阶段 1: spec 文档

- [x] 1.1 `proposal.md`：动机、What Changes、Scope、Out of Scope、Open Questions
- [x] 1.2 `specs/platform-test-contract/spec.md`：R1–R7 ADDED scenario + 平台 → fixture 路径表 + Deferred 段（threading）
- [x] 1.3 `design.md`：4 个 Decision（contract 形式 / fixture 最小集 / sink 行为 / threading 不进 contract）
- [x] 1.4 `tasks.md`：本文件
- [x] 1.5 Open Questions 裁决：threading 不纳入 / smoke 充当 resolver 正路 / lifecycle 纳入

## 阶段 2: design doc 同步

- [x] 2.1 `docs/design/runtime/embedding.md` 新增 §0 "编译边界（host 编 / mobile 跑）"
- [x] 2.2 `docs/design/runtime/embedding.md` §12 Deferred 加 "Facade threading 测试（R8）"行
- [x] 2.3 `src/toolchain/host/platforms/README.md` 顶部加 "编译边界（重要）"段
- [x] 2.4 `docs/roadmap.md` Deferred Backlog Index 加索引行指向 embedding.md §12

## 阶段 3: 归档

- [x] 3.1 把 changes/define-platform-test-contract/ 移到 archive/2026-05-12-define-platform-test-contract/
- [x] 3.2 commit + push（type = docs，scope = spec）

## 备注

- 本 spec **不产代码**，只产文档。三个下游 spec (`add-ios-tests` / `add-android-tests` / `add-wasm-tests`) 才是实际实施。
- `multi_line.z42` 例子源文件归到第一个落地下游 spec（避免 contract spec 引入实施代码）。
- threading-tests 等 v0.1 runtime 引入正式 threading 模型后再回来补 R8 scenario。
