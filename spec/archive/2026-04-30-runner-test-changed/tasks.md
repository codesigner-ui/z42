# Tasks: scripts/test-changed.sh — Incremental Test Selection (R3c)

> 状态：🟢 已完成 | 归档：2026-04-30 | 创建：2026-04-30
> 类型：feature；最小化模式
> 依赖：无（纯 bash + git）

## 变更说明

实现 `scripts/test-changed.sh`，根据 `git diff` 把变更文件映射到受影响测试集合，替换 justfile 中 `test-changed` 占位。

## 原因

当前 justfile 的 `just test-changed` 是 P2 placeholder，跑就 exit 1。完整 `just test` ~50s（编译器 ~3s + VM goldens ~30s + stdlib ~10s + cross-zpkg ~5s），本身已经不慢；但本地 dev 反复改一个 stdlib lib 时，跑全套仍有冗余。

R3c 给开发者一个**精确范围工具**：改 `z42.math/src/*` 只触发 `z42.math` 测试 + VM goldens（覆盖 IR 层潜在 regression）；改 `src/compiler/` 触发 dotnet test + 所有 VM goldens；只改文档不触发任何测试。

不做编译期反向依赖图（"改 ClassA 触发用 ClassA 的所有测试"）—— 那需要 IR 级数据流分析。本期只做**目录级粗粒度映射**。

## 文档影响

- `docs/dev.md` 加 `just test-changed` 用法
- `docs/design/testing.md` "增量测试" 段（替换当前 placeholder 说明）
- `justfile` 注释更新

## 设计

### 变更检测

`git diff --name-only $BASE` 列出变更文件。`$BASE` 来源（按优先级）：

1. 环境变量 `Z42_TEST_CHANGED_BASE`（CI / 自定义）
2. 命令行第一参数（`./scripts/test-changed.sh main`）
3. 默认 `HEAD`（即工作区相对最近 commit 的修改 + 已 staged + 未 staged）

只考虑 working tree + staged，不考虑历史 commits（保持简单）。

### 文件 → 测试映射规则（按优先级，先匹配先生效）

| 变更文件 | 触发命令 |
|----------|---------|
| `src/libraries/<lib>/src/**` | `just test-stdlib <lib>` + `just test-vm` |
| `src/libraries/<lib>/tests/**` | `just test-stdlib <lib>` |
| `src/libraries/<lib>/<lib>.toml`（manifest）| `just test-stdlib <lib>` |
| `src/runtime/src/**`、`src/runtime/Cargo.toml` | `cargo test --manifest-path src/runtime/Cargo.toml` + `just test-vm` |
| `src/compiler/**` | `just test-compiler` + `just test-vm` |
| `src/toolchain/**` | `cargo test --manifest-path src/toolchain/test-runner/Cargo.toml` + `just test-stdlib` |
| `scripts/test-vm.sh`、`scripts/regen-golden-tests.sh` | `just test-vm` |
| `scripts/test-stdlib.sh` | `just test-stdlib` |
| `scripts/test-cross-zpkg.sh`、`src/runtime/tests/cross-zpkg/**` | `just test-cross-zpkg` |
| `*.md`、`docs/**`、`spec/**`、`.claude/**`、`README*` | （不触发任何测试） |
| 其他 | 全套：`just test` |

去重：同类命令收集到 set，最终一个 unique 列表执行。

### 输出与行为

```
$ just test-changed
[test-changed] base = HEAD
[test-changed] changed files (3):
  src/libraries/z42.math/src/Math.z42
  src/libraries/z42.math/tests/math_basics.z42
  README.md
[test-changed] plan:
  → just test-stdlib z42.math
  → just test-vm
[test-changed] running...
... (output of each command)
[test-changed] result: ok
```

`--dry-run` 模式：只打印 plan，不执行（pre-commit hook 友好）。

### 退出码

- `0` — 全部命令通过（或没有命令要跑）
- 命令的非 0 退出码透传（first failure 即停）
- `2` — 工具错误（git diff 失败、git 仓库不存在）

## 检查清单

- [x] 1.1 [scripts/test-changed.sh](scripts/test-changed.sh) NEW（设计如上；bash + git diff）
    - `BASE` 解析（env > arg > HEAD）
    - 文件分类 → command set 收集
    - 顺序执行命令；first failure exit
    - `--dry-run` flag 跳过执行
    - 工作区无变更：打印 "no changed files"，exit 0
- [x] 1.2 [justfile](justfile) `test-changed` task：
    - 删除 placeholder
    - 接 `./scripts/test-changed.sh {{args}}`，args 透传
- [x] 2.1 手动测试：
    - 无变更 → exit 0，打印 "no changed files"
    - 改 docs → exit 0，no commands
    - 改 z42.math 源 → 触发 z42.math + VM
    - 改 compiler → 触发 compiler + VM
    - 改 runtime/src → 触发 cargo test + VM
    - 多目录混改 → 命令去重 + 顺序执行
    - `--dry-run` 不实际执行
- [x] 3.1 [docs/dev.md](docs/dev.md) 添加 `just test-changed` 示例
- [x] 3.2 [docs/design/testing.md](docs/design/testing.md) 增量测试段：列出映射规则 + 限制
- [x] 3.3 [docs/roadmap.md](docs/roadmap.md) M6 R3c 完成；增量测试占位符移除
- [x] 4.1 commit + push + 归档

## Out of scope

- ⏸️ 编译期反向依赖图（IR/类级粒度的 "改 ClassA → 触发所有 use ClassA 的测试"）
- ⏸️ 缓存上次测试结果（只跑失败/未跑过）
- ⏸️ 多 base 比对（只支持一个 base ref）
- ⏸️ 跨 commit 范围（只考虑工作区 + staged）
- ⏸️ Watch 模式（`just test-watch`）

## 备注

- 用 bash 实现，不引入新工具依赖（保持与现有 scripts/* 风格一致）
- bash 4+ 关联数组用法在 macOS 默认 bash 3.x 不可用——用 set-via-string 模式（cmd 列表用 `\n` 分隔，`sort -u` 去重）
- 严格按文件路径前缀匹配；不读 Cargo.toml / .toml 实际依赖图
- pre-commit hook 集成示例放 docs/dev.md
