# Tasks: 定义跨平台 SDK package 目录约定（per-arch flat）

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-13 | 类型：lang-adjacent docs（无代码，纯规范契约）

## 进度概览

- [x] 阶段 1: spec 文档（proposal / spec / design / tasks）
- [x] 阶段 2: design doc 同步（embedding.md §11.9 + roadmap.md）
- [x] 阶段 3: 归档

## 阶段 1: spec 文档

- [x] 1.1 `proposal.md`：动机、What Changes、Scope、Out of Scope、9 个 decision 摘要
- [x] 1.2 `specs/package-layout/spec.md`：Requirement 1–9 + Modified 1（embedding.md §11.9）
- [x] 1.3 `design.md`：D1–D9 决策落字 + cross-cutting + Deferred 3 项
- [x] 1.4 `tasks.md`：本文件

## 阶段 2: design doc 同步

- [x] 2.1 `docs/design/runtime/embedding.md` §11 末尾加 §11.9 "分发 package 形态"，列 13 个 per-arch package + 引向本 spec
- [x] 2.2 `docs/roadmap.md` "横向工作流" 表加一行：跨平台 package 分发（Phase 1 完成后 enable）
- [x] 2.3 `docs/roadmap.md` Deferred Backlog Index 加 3 行：multi-arch-container-packages / per-arch-abi-feature-matrix / binary-package-signing

## 阶段 3: 归档

- [x] 3.1 移 `changes/define-package-layout/` → `archive/2026-05-13-define-package-layout/`
- [x] 3.2 commit + push（type=docs，scope=spec）

## 备注

- 本 spec **不产代码**，只产规范契约。下游 Phase 1.1–1.4 共 4 个 spec 才是实施。
- D1–D9 + Requirement 1–9 是下游 spec 实施时的硬性契约；冲突即违规。
- byte-identical invariant（libs / native/include / examples/hello_c/main.c）通过 SHA-256 check 验证（下游 spec 阶段 6 GREEN gate 加 step）。
- abi-version 在所有 package 标 = 1；未来升 2 时所有 package 同步升。
- **关键决策**：选 per-arch flat (D2/D3/D8) + 砍 facade/ (D7) + 砍 embedding-c spec (D9) → Phase 1 从 6 spec 缩到 5（含本 1.0）。
- Phase 2 deferred convenience：multi-slice xcframework / multi-ABI AAR；用户呼声出来再加。
