# Proposal: Redesign Test Infrastructure (Test/Bench/Cross-Platform)

## Why

当前 z42 的测试与构建基础设施有三类痛点，正在阻碍后续迭代速度：

1. **测试用例混在 [src/runtime/tests/golden/run/](src/runtime/tests/golden/run/)**：103 个 golden 用例同时考验语法、类型、VM 执行、stdlib 调用。任何改动都要跑全部 100+ 用例，定位慢；stdlib 各库无本地测试，完全靠隐式覆盖。
2. **缺少 benchmark 与 CI**：性能回归无追踪手段；7 个独立 shell 脚本无统一入口；无自动 CI。
3. **无跨平台支持**：纯 x64 桌面，无 wasm / iOS / Android 工程脚手架与 feature gate；Cranelift JIT 与平台耦合，未做 feature 切分。

不解决这些问题，新增 stdlib / 新增语言特性的开发体验会持续退化，且后续移动端 / Web 端落地无路径。

**本变更交付的是「全局规划 + 4 份正式 spec 文档」**，把工作拆成 5 个独立 Phase，每个 Phase 后续单独立 `spec/changes/<name>/` 落地。本变更**不交付任何代码**，只交付：proposal、design、spec、tasks 四份文档以及目录占位。

## What Changes

- 新建 [spec/changes/redesign-test-infra/](spec/changes/redesign-test-infra/) 容器
- 写出本次重构的 **proposal.md / design.md / specs/test-infrastructure/spec.md / tasks.md**
- 在 design.md 中钉死：测试归属规则、工具链入口约定、benchmark 框架、CI 形态、跨平台 feature 矩阵、5 个 Phase 顺序
- 在 tasks.md 中列出 5 个 Phase 的子 spec 占位与依赖关系
- 不修改任何源码、不创建工程目录、不执行迁移

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| [spec/changes/redesign-test-infra/proposal.md](spec/changes/redesign-test-infra/proposal.md) | NEW | 本文件 |
| [spec/changes/redesign-test-infra/design.md](spec/changes/redesign-test-infra/design.md) | NEW | 完整设计：归属规则、工具链、benchmark、CI、跨平台、Phase 计划 |
| [spec/changes/redesign-test-infra/specs/test-infrastructure/spec.md](spec/changes/redesign-test-infra/specs/test-infrastructure/spec.md) | NEW | 可验证场景：每个 Phase 完成后的对外可观察行为 |
| [spec/changes/redesign-test-infra/tasks.md](spec/changes/redesign-test-infra/tasks.md) | NEW | 实施清单：5 个 Phase 的子 spec 占位与顺序 |

**只读引用**（理解上下文，不修改）：

- [docs/dev.md](docs/dev.md) — 现有构建/测试命令
- [src/runtime/tests/](src/runtime/tests/) — 现有 golden 测试目录
- [src/compiler/z42.Tests/](src/compiler/z42.Tests/) — 现有 xUnit 测试项目
- [src/libraries/](src/libraries/) — 6 个 stdlib 子目录
- [scripts/](scripts/) — 7 个现有 shell 脚本
- [src/runtime/Cargo.toml](src/runtime/Cargo.toml) — 现有依赖与 target 配置

## Out of Scope（本变更不做）

- **任何代码或目录创建**：不写 justfile、不建 `platform/`、不动 `src/runtime/tests/`、不改 Cargo features
- **现有 golden 用例的迁移**：只规定迁移规则，迁移本身由后续 Phase 3 spec 执行
- **编译器测试改造**：自举完成前保留 C# xUnit 现状，不做迁移规划之外的设计
- **CI 平台选型最终确认**：design.md 推荐 GitHub Actions，但具体落地由 Phase 0 spec 单独决定
- **Cranelift Android 兼容性实测**：本变更只规定 Android 默认 interp-only，JIT 的实测推迟到 Phase 4 之后单独评估

## Open Questions

无（关键决策已在交互中由 User 裁决：
- stdlib runner = 自建 z42-test-runner ✅
- 任务编排 = `just` ✅
- 跨平台优先级 = wasm > Android > iOS ✅
- CI 与基础设施一起做 ✅
- 编译器 golden 与 runtime golden 暂不合并，先标记重叠 ✅）
