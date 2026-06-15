# Proposal: golden run-`.zbc` 重定向到 artifacts + 清理 src 残留构建产物

## Why

src 源码树里散落 320 个 gitignored `.zbc`（+ 3 个 `dist/`、108 个 `.cache/`），违反"所有构建产物统一落 `artifacts/`、不污染 src"的既定约定。来源有三类：

1. **单包独立构建残留**（`z42c build <pkg>` 未走 workspace 重定向）：`src/libraries/{z42.core,z42.io,z42.compression}/.cache/` + `/dist/`（Jun 9 一次性产物）。
2. **test-lib 旧路径死文件**：`src/libraries/<lib>/tests/*.zbc`（192 个，2026-06-07 修复前残留；该路径现已正确写 `artifacts/build/libraries/<lib>/release/tests/dist/`）。
3. **VM golden run-fixture**：`src/{tests,libraries/<lib>/tests}/<name>/source.zbc`（regen 产物，至今仍写在 `source.z42` 旁）。

第 1、2 类是历史残留，删除即可。第 3 类需把 regen 的输出落点从源码旁改到 `artifacts/` 镜像，并同步两个读者。

> **例外**：`src/tests/zbc-format/*/source.zbc`（6 个）是**故意 check-in** 的字节基线 golden（`git diff` 当格式漂移检测），**不动**。

## What Changes

- regen 把 run-golden 的 `.zbc` 写到 `artifacts/build/golden/<src 相对路径>/`（镜像 src 布局），`zbc-format` 类目仍就地写 src。
- `test vm` golden runner 从该 artifacts 镜像读取 run-golden `.zbc`。
- Rust `zbc_compat` 跨语言契约测试：committed `zbc-format` 仍读 src，run-golden 改读 artifacts 镜像。
- 删除 src 下全部三类残留产物（共 320 `.zbc` + 3 `dist/` + 3 `.cache/`）。
- 重建 `artifacts/xtask/xtask.zpkg` 使新逻辑生效。
- 文档同步约定变更。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/xtask_golden.z42` | MODIFY | 新增共享 helper `_goldenArtifactDir` |
| `scripts/xtask_regen.z42` | MODIFY | run-golden 输出 → artifacts 镜像；zbc-format 仍就地 |
| `scripts/xtask_test_vm.z42` | MODIFY | golden runner 从镜像读 `.zbc`（dir part1/part2 + flat） |
| `src/runtime/tests/zbc_compat.rs` | MODIFY | run-golden 发现路径 → artifacts 镜像；zbc-format 仍读 src |
| `.gitignore` | MODIFY | 补 `artifacts/` 已覆盖确认 + 移除/保留 src `.zbc` 兜底说明 |
| `docs/design/testing/testing.md` | MODIFY | golden `.zbc` 落点约定更新 |
| `src/tests/README.md` | MODIFY | source.zbc 落点说明 |
| `scripts/README.md` | MODIFY | regen 产物路径说明 |

**只读引用**：

- `src/compiler/z42.Tests/GoldenTests.cs` — 确认其从 `source.z42` 内存编译、不读 `source.zbc`（不受影响）
- `.github/workflows/ci.yml` — 确认 regen 先于 cargo test / test vm（顺序已满足，无需改）

## Out of Scope

- `src/tests/zbc-format/*/source.zbc`（committed 字节基线）—— 不动
- test-lib 编译路径（已于 2026-06-07 正确写 artifacts）—— 不改逻辑
- 单包独立构建的 output 重定向根因（per-package toml 加 `[build]`）—— 残留删除即可；独立构建非 GREEN 流程步骤，留待后续按需处理

## Open Questions

- 无
