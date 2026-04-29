# Proposal: Add `just` Task Runner & GitHub Actions CI

## Why

当前仓库有 7 个独立 shell 脚本（`scripts/test-vm.sh`、`scripts/test-cross-zpkg.sh`、`scripts/build-stdlib.sh` 等）和**完全无 CI**。新成员需要查阅 [docs/dev.md](docs/dev.md) 才能跑通验证流程；本地通过的改动可能在 PR 阶段才被发现回归。

后续 4 个 Phase（P1 benchmark、P2 test-runner、P3 用例迁移、P4 跨平台）都依赖：
1. 一个统一入口（决定每个 Phase 的命令命名与组织）
2. 一套自动跑全绿验证的 CI（决定回归保护的时机）

P0 不引入新的测试或 benchmark 能力，**只**搭这两个基础设施，让后续 Phase 接入即可。

## What Changes

- **新增 [justfile](justfile)**：定义 5 类顶层 task —— `test` / `bench` / `build` / `clean` / `ci`，外加 `platform` 占位（P4 接入）
- **接入现有 7 个脚本**：每个 just task 内部直接调用 `scripts/*.sh`，**不删除、不重写脚本逻辑**
- **新增 [.github/workflows/ci.yml](.github/workflows/ci.yml)**：3 平台矩阵（linux-x64 / macos-aarch64 / windows-x64），跑 `just build` + `just test`
- **缓存策略**：`actions/cache` 缓存 cargo target、NuGet packages、stdlib 构建产物
- **文档更新**：[docs/dev.md](docs/dev.md) 顶部加"快速入门：使用 just"小节；[README.md](README.md) 加 just 提及

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `justfile` | NEW | 顶层任务定义；接入 7 个现有脚本 |
| `.github/workflows/ci.yml` | NEW | PR + push to main 触发；3 平台矩阵 |
| `.github/workflows/README.md` | NEW | workflows 目录说明（按 code-organization 规则） |
| `docs/dev.md` | MODIFY | 顶部加 "Quick Start: just" 小节 |
| `README.md` | MODIFY | 提及 `just --list` 作为入口 |
| `.gitignore` | MODIFY | 忽略 just 可能的 .env 文件（若有） |

**只读引用**：

- [scripts/test-vm.sh](scripts/test-vm.sh) — 理解参数与 exit code
- [scripts/test-cross-zpkg.sh](scripts/test-cross-zpkg.sh) — 同上
- [scripts/build-stdlib.sh](scripts/build-stdlib.sh) — 同上
- [scripts/package.sh](scripts/package.sh) / [scripts/test-dist.sh](scripts/test-dist.sh) / [scripts/regen-golden-tests.sh](scripts/regen-golden-tests.sh) — 同上
- [docs/dev.md](docs/dev.md) — 现有命令清单
- [src/runtime/Cargo.toml](src/runtime/Cargo.toml) — 仅读，不在本 spec 修改

## Out of Scope

- **benchmark 实现**（P1）：justfile 暂不暴露 `bench` 子命令的实际 task body，只占位 `bench: @echo "P1 待实施"`
- **z42-test-runner**（P2）：`just test changed` 命令暂不实现，只占位
- **用例迁移**（P3）：`just test stdlib <lib>` 子命令暂不实现
- **跨平台 task**（P4）：`just platform <name> <action>` 暂不实现
- **CI 中的 benchmark 门禁**（P1）：本 spec 的 CI 只跑 build + test
- **删除现有脚本**：保留所有 `scripts/*.sh`，只在 justfile 中调用
- **Windows 路径兼容**：现有脚本是 bash；本 spec **不**为 Windows runner 写 PowerShell 平行版本，windows-x64 矩阵格上**只跑 cargo build / dotnet build**，不跑 shell 脚本（验证 Rust + .NET 能编译即可）

## Open Questions

- [ ] **Q1**：是否在 CI 中开启 `dotnet build /warnaserror` 与 `cargo build --deny-warnings`？
  - 倾向：本 spec 不开（避免拉入大量 pre-existing warning 修复）；后续单独立 spec
- [ ] **Q2**：CI 是否上传 artifact（编译好的 .zpkg / .zbc）？
  - 倾向：本 spec 不上传（节省 CI 资源）；P1 引入 baseline JSON 时才考虑
- [ ] **Q3**：是否引入 `pre-commit` hook？
  - 倾向：本 spec 不引入；后续单独立 spec 评估
