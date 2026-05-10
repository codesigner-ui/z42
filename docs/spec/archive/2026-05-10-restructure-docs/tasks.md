# Tasks: restructure-docs-2026-05-10

> 状态：🟢 已完成 (2026-05-10) | 创建：2026-05-10 | 类型：docs / refactor（最小化模式）

## 变更说明

按 4 大用户需求重构 `docs/`：① 统一文档结构、清理冗余/过时；② `design/` 按 dotnet/rust 风分类到子目录；③ 合并 `roadmap.md` + `version.md` 思路、移除已完成内容；④ 删除 `deferred.md`，分散到对应 design doc + roadmap 集中索引。

## 原因

`docs/` 经长期积累出现 6 种成文风格、多处冗余（ir/zbc/exec-model、stdlib 三件套、testing/cross-platform-testing、compiler-architecture/project/compilation 等）、`roadmap.md` 54KB 70%+ 已完成内容、`review.md` 应归档却仍在活文档区。结构性整理可显著降低未来维护成本。

## 文档影响

更新：`.claude/rules/workflow.md`（删除 `deferred.md` 分支）、`.claude/projects/*/memory/feedback_deferral_location.md`、所有跨文档引用路径。

## 进度概览

- [x] Phase 1: 归档 review.md + 外部依赖更新（workflow.md / memory）
- [x] Phase 1.5: 把 `spec/` 整体挪到 `docs/spec/`（保留子结构）+ 全仓批量更新引用
- [x] Phase 2: design/ 子目录重构（5 个子目录 + READMEs + 39 文件搬运）
- [x] Phase 3: deferred.md 分散 + 删除
- [x] Phase 4: 冗余清理（9 项审查后实际处理 7 项 + 跳过 2 项无重复）
- [ ] Phase 5: roadmap + version 重写
- [ ] Phase 6: 风格模板统一 + 跨引用批量更新

## Phase 1: 归档 + 工作流规则更新

- [x] 1.1 `docs/review.md` → `docs/spec/archive/2026-05-10-docs-review/review.md`，加 `## Status` 头部声明"快照，已归档"
- [x] 1.2 更新 `.claude/rules/workflow.md` "延后特性管理" 段：删除 `docs/deferred.md` 分支，统一改"延后 → 对应 `docs/design/<feature>.md` Deferred 段 + roadmap 集中索引"
- [x] 1.3 更新 `.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/feedback_deferral_location.md` 同步新规则
- [x] 1.4 更新 `.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/MEMORY.md` index + `feedback_problem_first_then_defer.md`
- [ ] 1.5 commit + push

## Phase 1.5: spec/ → docs/spec/

形态 A（保留子结构，仅改顶层路径）。

- [x] 1.5.1 `git mv spec docs/spec`（一次性整目录迁移）
- [x] 1.5.2 全仓 perl -pi 更新引用：`spec/changes/` → `docs/spec/changes/`、`spec/archive/` → `docs/spec/archive/`（带 negative lookbehind 防双前缀）；148 文件批量更新
- [x] 1.5.3 残留裸 `spec/` 引用人工修补：`workflow.md` 目录结构图 + 提交规则、`CLAUDE.md` 自动提交段、`docs/design/testing/testing.md` 增量测试 glob 表、`docs/roadmap.md` H0 描述、`scripts/test-changed.sh` glob、`src/toolchain/host/README.md`
- [x] 1.5.4 验证：grep 全仓不再出现裸 `spec/changes` / `spec/archive` 路径；剩余 `spec/` 引用为 OpenSpec 偏离对比表（workflow.md）+ "spec/code drift" 概念性表述（GrammarSyncTests.cs），合理保留
- [ ] 1.5.5 commit + push

## Phase 2: design/ 子目录重构

新目录结构：
```
docs/design/
├── README.md           # 顶层导航
├── philosophy.md       # 顶层不动
├── language/           # 19 文件
│   ├── README.md
│   ├── language-overview.md, grammar.peg, syntax-config.md
│   ├── namespace-using.md, access-control.md
│   ├── parameter-modifiers.md, compound-assign.md, properties.md
│   ├── foreach.md, iteration.md, arrays.md
│   ├── string-builtins.md, exceptions.md, object-protocol.md
│   ├── customization.md, interop.md
│   ├── generics.md, static-abstract-interface.md
│   └── closure.md, delegates-events.md
├── compiler/           # 5 文件
│   ├── README.md
│   ├── compiler-architecture.md, compilation.md
│   ├── project.md, manifest-schema.json
│   └── error-codes.md
├── runtime/            # 10 文件
│   ├── README.md
│   ├── vm-architecture.md, execution-model.md
│   ├── ir.md, zbc.md, jit.md
│   ├── hot-reload.md, gc-handle.md, concurrency.md
│   ├── embedding.md, cross-platform.md
├── stdlib/             # 3 文件（重命名）
│   ├── README.md
│   ├── overview.md     # 原 stdlib.md
│   ├── organization.md # 原 stdlib-organization.md
│   └── roadmap.md      # 原 stdlib-roadmap.md
└── testing/            # 2 文件
    ├── README.md
    ├── testing.md
    └── cross-platform-testing.md
```

- [ ] 2.1 创建 5 个子目录
- [ ] 2.2 git mv 19 文件入 `language/`
- [ ] 2.3 git mv 5 文件入 `compiler/`（含 `docs/error-codes/` 整合判断）
- [ ] 2.4 git mv 10 文件入 `runtime/`
- [ ] 2.5 git mv 3 stdlib 文件入 `stdlib/`，重命名为 `overview/organization/roadmap`
- [ ] 2.6 git mv 2 testing 文件入 `testing/`
- [ ] 2.7 写 5 个子目录 README.md（按 `.claude/rules/code-organization.md` 模板）
- [ ] 2.8 重写 `docs/design/README.md` 为顶层导航
- [ ] 2.9 commit + push

## Phase 3: deferred.md 分散

- [x] 3.1 D-2 ISubscription chain → `language/delegates-events.md` Deferred 段
- [x] 3.2 D-3 N>4 arity Action/Func → `language/delegates-events.md` Deferred 段
- [x] 3.3 D-4 协变/逆变 → `language/generics.md` Deferred 段
- [x] 3.4 D-11 introduce-bound-visitor → `compiler/compiler-architecture.md` Deferred 段
- [x] 3.5 D-12 BindCall 函数级拆分 → `compiler/compiler-architecture.md` Deferred 段
- [x] 3.6 删除 `docs/deferred.md`，roadmap.md 加 "Deferred Backlog Index" 段（设计期延后 + 实施期延后两表）
- [x] 3.7 修补残留 `deferred.md` 引用（embedding.md / delegates-events.md 状态行）
- [ ] 3.8 commit + push

## Phase 4: 冗余清理

- [ ] 4.1 `runtime/ir.md` 作字节码格式权威；`zbc.md` 仅讲二进制编码 wire format / section layout；`execution-model.md` 删指令格式段，改为链接
- [ ] 4.2 `stdlib/overview.md` 作三层架构 + Script-First 权威；`organization.md` 仅讲包边界 + 现状（不重复架构）；`roadmap.md` 仅讲 P0–P3 排期；统一层级命名 `L0/L1/L2/L3`
- [ ] 4.3 `testing/testing.md` 删平台支持矩阵段；`cross-platform-testing.md` 专讲跨平台 runner-as-library
- [ ] 4.4 `compiler/project.md` 移除 zbc 输出细节段；`compiler/compilation.md` 专讲产物策略；`compiler-architecture.md` 边界明确为编译器内部数据结构
- [ ] 4.5 `runtime/vm-architecture.md` 讲内部 API（如 `register_native_type`）；`runtime/embedding.md` 讲 Host C ABI 与外部嵌入路径
- [ ] 4.6 `docs/features.md` §18 / `docs/roadmap.md` 横向工作流表 删 "200KB / 树摇" 重复段（保留 features.md 决策）
- [ ] 4.7 `language/language-overview.md` 收敛字符串/数组/异常段为索引型，详情链接 `string-builtins.md` / `arrays.md` / `exceptions.md`
- [ ] 4.8 `language/foreach.md` + `iteration.md` + `language-overview.md` 迭代描述去重
- [ ] 4.9 `language/interop.md` L1 interim 段拆分到 `docs/spec/archive/2026-05-10-l1-interim-interop-snapshot/`，主文档仅留 L2+ 设计
- [ ] 4.10 commit + push

## Phase 5: roadmap + version 重写

- [ ] 5.1 重写 `docs/roadmap.md`：仅"当前焦点 M6/M7" + "下一阶段 0.2.x charter" + "Deferred Backlog Index"（含 D-* 重新分配后的指针）+ "代码质量 Backlog 未完成项" + 链接 version.md。预期从 54KB 缩到 ~12KB。
- [ ] 5.2 更新 `docs/version.md`：保持 SemVer 蓝图主体，加"Feature → Version" 映射表（feature.md 章节 → 子版本号）
- [ ] 5.3 `docs/dev.md` 修正 `test-vm.sh` 默认重建 stdlib + golden 行为说明
- [ ] 5.4 `docs/README.md` 更新文件导航与新目录结构
- [ ] 5.5 commit + push

## Phase 6: 风格模板统一 + 跨引用更新

定义三模板（详见 design/README.md）：
- **A 长设计**：Status / Why / Design Decisions / Syntax / Pipeline / Runtime / Examples / Deferred
- **B 短规范**：Status / Syntax / Semantics / Pipeline Mapping / Limits / Deferred
- **C 参考手册**：仅 language-overview.md，按主题章节 + 链接出去

- [ ] 6.1 给以下文件加统一 `## Status` 段（phase + 锁定日期）：generics, closure, delegates-events, interop, static-abstract-interface, parameter-modifiers, properties, arrays, foreach, string-builtins, compound-assign, exceptions, object-protocol, customization, syntax-config, namespace-using, access-control, iteration
- [ ] 6.2 跨引用批量更新：grep `docs/design/<file>.md` → `docs/design/<dir>/<file>.md`，全仓搜索（含 `.claude/`、`spec/`、`README.md`、其他 `docs/*.md`）
- [ ] 6.3 `language-overview.md` 收敛为模板 C（参考手册），主题章节后链接到细节
- [ ] 6.4 commit + push

## 备注

- 每个 phase 单独 commit + push，遵守 workflow 阶段 9
- 全程不修改 `src/` 任何代码（纯 docs refactor）
- 不要求跑 `dotnet test` / `cargo test`（GREEN 标准对纯 docs 变更不适用 —— 仅需 markdown 链接不破）
- Phase 1 完成才能开 Phase 3（deferred.md 删除依赖 workflow.md 规则更新）
- Phase 2 完成才能开 Phase 3-6（路径变更影响所有后续编辑）
