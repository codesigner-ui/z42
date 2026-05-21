# Tasks: Scope-aware test-all.sh

> 状态：🟢 已完成 | 创建：2026-05-21 | 完成：2026-05-21 | 类型：workflow

## 进度概览
- [x] 阶段 1: test-all.sh 解析 --scope flag + STAGES 按 scope 构造
- [x] 阶段 2: resolve_scope_from_diff helper（auto-detect）
- [x] 阶段 3: docs-only 路径 (0 stages clean exit)
- [x] 阶段 4: 手动验证各 scope
- [x] 阶段 5: workflow.md 文档同步
- [x] 阶段 6: 归档 + commit + push

## 阶段 1: --scope arg + STAGES

- [x] 1.1 `scripts/test-all.sh` 加 `SCOPE=full` 默认值 + `--scope=<value>` 解析
- [x] 1.2 case "$SCOPE" 分支生成 STAGES（full / runtime / compiler / stdlib / docs-only）
- [x] 1.3 unknown scope → 错误退出 2

## 阶段 2: auto-detect

- [x] 2.1 加 `resolve_scope_from_diff` 函数：`git diff --name-only HEAD` 按路径前缀分类
- [x] 2.2 `SCOPE=auto` → 调 resolve + 输出 "auto-detected scope: X"
- [x] 2.3 路径分类：src/compiler → compiler；src/runtime + src/tests → runtime；src/libraries + examples → stdlib；docs + .claude → 不升级 scope（默认 docs-only）；其他 → full（保守）
- [x] 2.4 has_compiler && has_runtime → full（混改）

## 阶段 3: docs-only path

- [x] 3.1 STAGES 空时输出 "✅ ALL GREEN (0 stages — docs-only)" + exit 0
- [x] 3.2 验证 docs-only 模式下脚本不调任何 stage

## 阶段 4: 手动验证

- [x] 4.1 `./scripts/test-all.sh --scope=runtime` —— 4 stages 跑通 (cargo + test-vm + cross-zpkg + stdlib)
- [x] 4.2 `./scripts/test-all.sh --scope=compiler` —— 5 stages 跑通
- [x] 4.3 `./scripts/test-all.sh --scope=stdlib` —— 3 stages 跑通
- [x] 4.4 `./scripts/test-all.sh --scope=auto` —— 检 git diff，resolve 合理 scope，stages 跑通
- [x] 4.5 `./scripts/test-all.sh` (no flag) —— 仍 6 stages（无回归）

## 阶段 5: 文档同步

- [x] 5.1 `docs/workflow/testing.md` 新增 "Scope-aware test-all" 段：列出 5 scope + 何时用哪个 + 例子
- [x] 5.2 `.claude/rules/workflow.md` 阶段 8 注：iteration 期允许 scope 缩窄；commit 前最终 GREEN 用 `--scope=full`

## 阶段 6: 归档 + commit

- [x] 6.1 mv → `docs/spec/archive/2026-05-21-add-test-split-by-area/`
- [x] 6.2 verify default behavior 不回归
- [x] 6.3 commit + push

## 备注

### 实施期发现 1 —— testing 文档已存在子目录结构

阶段 5.1 原计划在 `docs/workflow/testing.md` 加段落，但发现项目 testing 文档已拆为
`docs/workflow/testing/` 子目录（README.md + 5 个子主题）。改为 update
`testing/README.md` 加 "Scope-aware test-all" 段。

### 实施期发现 2 —— `--quick` 与 `--scope` 兼容性

design Decision 5 说两者正交。实施期保留：`--quick` 通过 `STAGE_VM_GOLDENS`
变量内部条件 `$QUICK && echo '--no-rebuild'` 注入；`--scope` 选 stage 集合。
两者独立可组合，无 conflict。

### 实施期发现 3 —— `auto` 路径未升级时退化为 docs-only

`resolve_scope_from_diff` 当只有 docs/.claude 改时返 `docs-only`，对应 0 stages。
spec 阶段 3 docs-only 路径已 cover；测试 `./scripts/test-all.sh --scope=docs-only` 输出
`✅ ALL GREEN (0 stages — docs-only)` 验证通过。

### 后续 spec 占位

- `add-test-parallel-stages` —— 在 scope 内并行跑独立 stages（dotnet build 与
  cargo build 可并行；之后 dotnet test / test-vm / cross-zpkg / stdlib 也可并行）。
  与本 spec 正交叠加；orthogonal speedup 进一步压缩 GREEN gate 时间。User
  已在 2026-05-21 对话提出，独立 spec 跟进。
