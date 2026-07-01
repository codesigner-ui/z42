# 第二部分 · 编译器（Compiler）

z42c 自举编译器（用 z42 写，编译为 zpkg）的架构：pipeline 各阶段、产物布局、自举/种子机制、错误码体系。

## 章节规划

| 章节 | 涵盖 |
|------|------|
| 自举与种子 | 自举鸡蛋问题、warm/cold 种子、分阶段引入新语法纪律 |
| pipeline 各阶段 | 词法/语法/语义/binder/codegen 组织与数据流 |
| zpkg 产物与依赖索引 | 编译产物布局、依赖索引构建 |
| 错误码体系 | Z#### 错误码约定 |

## 迁移状态（旧 `docs/design/compiler/` → 本部分）

> ⬜ 待迁 · 🟡 迁移中 · ✅ 已迁并校对。

| 旧文档 | 目标章节 | 状态 |
|--------|---------|------|
| compiler-architecture.md | pipeline 各阶段（主干） | ⬜ |
| compilation.md / binder-hierarchy.md | pipeline 各阶段 | ⬜ |
| self-hosting.md | 自举与种子 | ⬜ |
| build-artifacts-layout.md / project.md | zpkg 产物与依赖索引 | ⬜ |
| error-codes.md | 错误码体系 | ⬜ |
| scripting-charter.md | pipeline 各阶段（脚本模式） | ⬜ |
