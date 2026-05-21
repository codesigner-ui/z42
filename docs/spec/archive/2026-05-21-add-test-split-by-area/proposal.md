# Proposal: Scope-aware test-all.sh — skip irrelevant stages by area

## Why

The current `./scripts/test-all.sh` runs all 6 stages unconditionally:
dotnet build + cargo build + dotnet test + test-vm + test-cross-zpkg +
test-stdlib. Full pipeline ≈ 3-5 minutes. Multiple specs in a session ×
2 runs per spec = 30+ minutes of cumulative wait per session.

**Real impact**: this current 12-spec arc (multi-threading + GC safepoint
+ sync primitives) added 30-50+ minutes of test-all overhead spread
across the day. Most of those specs touched only the Rust runtime — the
dotnet build / dotnet test / test-cross-zpkg stages always passed
trivially because their inputs hadn't changed.

User explicitly suggested splitting tests by area (compiler vs runtime).
This spec gives the workflow a way to express scope so non-affected
stages can be skipped explicitly. **Default behavior unchanged**
(full pipeline) for safety — opt-in flags for fast iteration.

## What Changes

- **New flag `--scope=runtime`** — skip compiler stages:
  - Skip: dotnet build, dotnet test
  - Keep: cargo build, test-vm, test-cross-zpkg, test-stdlib
- **New flag `--scope=compiler`** — skip cargo + runtime-only stages
  not affected by compiler-only changes:
  - Skip: cargo build (binary already built)
  - Keep: dotnet build, dotnet test, test-vm, test-cross-zpkg, test-stdlib
  - (Compiler change affects emitted .zbc → still need test-vm + stdlib)
- **New flag `--scope=stdlib`** — skip both build stages + compiler tests:
  - Skip: dotnet build, cargo build, dotnet test
  - Keep: test-vm, test-cross-zpkg, test-stdlib
  - For pure .z42 stdlib source edits (artifacts already built)
- **`--scope=full` (default)** — same as current behavior; no opt-in
  needed, no breaking change
- **Auto-detect via `--scope=auto`**: introspect `git diff --name-only HEAD`
  (or `HEAD~1..HEAD` for committed changes); pick the narrowest scope
  that covers all touched files
- **Workflow doc update** — `docs/workflow/testing.md` or section in
  `.claude/rules/workflow.md` documents when to use each scope

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/test-all.sh` | MODIFY | 加 `--scope=<runtime\|compiler\|stdlib\|full\|auto>` 参数解析；scope-driven STAGES 生成；auto-detect 走 `git diff --name-only` |
| `docs/workflow/testing.md` | MODIFY | 加 "Scope-aware test-all" 段说明 4 scope + auto；何时用哪个 |
| `.claude/rules/workflow.md` | MODIFY | 阶段 8（GREEN 标准）注 scope flag 可用；commit 前的最终 GREEN 必须用 full（或 auto 不缩窄） |
| `docs/spec/changes/add-test-split-by-area/` | NEW | 本 spec |

**只读引用**：

- `scripts/test-all.sh` 现 stage 定义
- `scripts/test-vm.sh` / `test-cross-zpkg.sh` / `test-stdlib.sh` 各 stage 依赖了解
- `docs/spec/archive/2026-05-21-add-gc-safepoint-counter-throttling/tasks.md` 实施期发现 2（user 要求 split 的起点）

## Out of Scope

- **CI workflow 改用 scope flag**：CI 应该跑 full（无人监督）；本 spec 不动 .github/workflows
- **Parallel stage execution**：不并发跑 stages，保持顺序+ early-stop 语义
- **Stage cache invalidation 自动跟踪**：scope 选哪个由 user / git 选；不引入 buck/bazel 风格 incremental
- **测试 fixtures 自动检测**：fixture 改变也算 stdlib scope，但本 spec 不引入 fixture-diff 解析；用 git diff 路径粗判够用

## Open Questions

- [ ] **auto-detect 的 baseline**：`HEAD` (uncommitted)、`HEAD~1..HEAD` (last commit)、`origin/main..HEAD` (PR branch)？Design Decision 1
- [ ] **scope 命名**：`--scope=runtime` 还是 `--runtime-only`？后者更口语；前者 unified 更清晰。Design Decision 2
- [ ] **default 保留 full 还是改 auto**：default=full 是 safe; default=auto 更快但可能让 user 漏跑相关 stage。Decision 3
