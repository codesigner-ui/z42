# 增量测试（只跑 git diff 影响）

dev 内循环加速：`z42 xtask.zpkg test changed` 根据 git diff 把改动文件映射到测试命令，去重后执行最小集合。

## 命令

```bash
z42 xtask.zpkg test changed                 # base = HEAD（unstaged + staged）
z42 xtask.zpkg test changed main            # base = main（branch 全部差异）
z42 xtask.zpkg test changed --dry-run       # 只打印计划，不执行
Z42_TEST_CHANGED_BASE=origin/main z42 xtask.zpkg test changed
```

## 文件 → 测试映射

| 改动 | 触发 |
|------|------|
| `src/libraries/<lib>/src/*` | `z42 xtask.zpkg test stdlib <lib>` + `z42 xtask.zpkg test vm` |
| `src/libraries/<lib>/tests/*` | `z42 xtask.zpkg test stdlib <lib>` |
| `src/runtime/src/*` / `Cargo.toml` / `build.rs` | `cargo test` + `z42 xtask.zpkg test vm` |
| `src/runtime/tests/*` | `cargo test` |
| `src/tests/cross-zpkg/*` | `z42 xtask.zpkg test cross-zpkg` |
| `src/tests/*` | `z42 xtask.zpkg test vm` |
| `src/compiler/*` | `z42 xtask.zpkg test compiler` + `z42 xtask.zpkg test vm` |
| `src/toolchain/*` | runner cargo test + `z42 xtask.zpkg test stdlib` |
| `*.md` / `docs/**` / `.claude/**` | 不触发 |
| `*.workspace.toml` / `build.rs` / `scripts/xtask*.z42` | 全套 `z42 xtask.zpkg test` |
| 其他 `src/**` | 全套 `z42 xtask.zpkg test`（防御性） |

完整规则见 [`docs/design/testing/testing.md`](../../design/testing/testing.md) "增量测试" 段。

## 局限

- 不考虑跨文件传递依赖（改 stdlib 内部 helper 不会触发依赖该 helper 的 cross-zpkg 测试）
- workspace toml / build.rs / xtask 源码触发全套（粗粒度）
- 适合**内循环加速**；推送前仍跑 `z42 xtask.zpkg test` 全套

## 实施

`z42 xtask.zpkg test changed` 用 `git diff --name-only <BASE>` 收集 tracked changes + `git ls-files --others --exclude-standard` 收集 untracked，去重后 case 映射 + dedup 命令 + 固定顺序执行（compile → vm → stdlib → cross）。

source: `z42 xtask.zpkg test changed`。
