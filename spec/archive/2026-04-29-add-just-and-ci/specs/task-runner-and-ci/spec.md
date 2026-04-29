# Spec: Task Runner & CI

## ADDED Requirements

### Requirement: 统一任务入口 (just)

#### Scenario: 列出所有任务

- **WHEN** 在仓库根目录执行 `just --list` 或 `just`
- **THEN** 输出包含 `build`、`test`、`bench`、`clean`、`ci`、`platform` 等顶层 task
- **AND** 每个 task 显示其简短说明（来自 just 的 doc comment）

#### Scenario: 全量构建

- **WHEN** 执行 `just build`
- **THEN** 依次运行 `cargo build --manifest-path src/runtime/Cargo.toml` 和 `dotnet build src/compiler/z42.slnx`
- **AND** 任一失败时 just 立即 exit 非零

#### Scenario: 全量测试

- **WHEN** 执行 `just test`
- **THEN** 依次运行 `dotnet test`、`./scripts/test-vm.sh`、`./scripts/test-cross-zpkg.sh`
- **AND** 任一失败时整体 exit 非零

#### Scenario: VM 测试切换执行模式

- **WHEN** 执行 `just test-vm jit`
- **THEN** 运行 `./scripts/test-vm.sh jit`，不影响 interp 默认
- **AND** `just test-vm`（不带参数）等价于 `just test-vm interp`

#### Scenario: 占位任务报告"待实施"

- **WHEN** 执行 `just bench`
- **THEN** 输出 "P1 待实施：benchmark"，exit 非零（避免误以为通过）
- **AND** `just test-changed`、`just test-stdlib`、`just test-integration`、`just platform <x> <y>` 同样行为

### Requirement: 向后兼容现有脚本

#### Scenario: 直接调用 shell 脚本

- **WHEN** 执行 `./scripts/test-vm.sh interp`
- **THEN** 行为与 just 接入前完全一致；不要求 just 已安装

#### Scenario: 脚本不被删除

- **WHEN** 检查 [scripts/](scripts/) 目录
- **THEN** 7 个 .sh 脚本全部保留：test-vm、test-cross-zpkg、build-stdlib、package、test-dist、regen-golden-tests、setup-dev

### Requirement: GitHub Actions CI

#### Scenario: PR 触发 CI

- **WHEN** 在 GitHub 上对 main 提 pull request
- **THEN** `.github/workflows/ci.yml` 自动启动
- **AND** 在 ubuntu-latest / macos-latest / windows-latest 三个 runner 上分别跑 build + test

#### Scenario: push to main 触发 CI

- **WHEN** push 或 merge 到 main
- **THEN** 同样的三平台 CI 自动启动

#### Scenario: Windows runner 退化到 smoke test

- **WHEN** CI 在 windows-latest runner 上运行
- **THEN** 不执行 `just test`（避免 bash 不可用）
- **AND** 改为运行 `dotnet test` + `cargo test`

#### Scenario: cargo / NuGet 缓存命中

- **WHEN** 同一分支 / 同一 Cargo.lock + csproj 的连续两次 CI run
- **THEN** 第二次 run 使用缓存的 cargo registry / target / NuGet packages
- **AND** Build 阶段总耗时较冷启动减少 ≥ 30%

#### Scenario: 任一 step 失败时 PR 检查标红

- **WHEN** 任一 build / test step 失败
- **THEN** 该 runner 的 job 标记为失败
- **AND** GitHub PR 页面 CI 检查显示红叉

### Requirement: 文档同步

#### Scenario: dev.md 含 just 快速入门

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 顶部存在 "Quick Start: just" 小节，列出 `just --list` / `just build` / `just test` 三条命令
- **AND** 现有详细命令清单保留在文档下半部分（向后兼容）

#### Scenario: README 提及 just

- **WHEN** 阅读 [README.md](README.md)
- **THEN** "构建与测试"段落首句为 `just --list` 提示
