# Spec: Release Automation

## ADDED Requirements

### Requirement: Tag push 触发跨平台 release pipeline

#### Scenario: 正式 release tag 触发

- **WHEN** 一个匹配 `v[0-9]+.[0-9]+.[0-9]+*` 的 tag 被 push 到 origin
- **AND** `versions.toml [project].version` 等于该 tag 去掉 `v` 前缀
- **THEN** GitHub Actions 运行 `.github/workflows/release.yml`
- **AND** 9 个 RID 的 SDK package 全部 build + archive 成功
- **AND** 最终产物上传到 GitHub Releases，URL 形如 `https://github.com/<owner>/z42/releases/tag/v<version>`
- **AND** Release 页面包含 9 个 archive + 1 个 `SHA256SUMS` 文件

#### Scenario: tag 与 versions.toml 漂移

- **WHEN** push 的 tag 是 `v0.2.5` 但 `versions.toml [project].version = "0.2.4"`
- **THEN** `verify` job 在第一步 fail，错误信息形如 `drift: tag=0.2.5 versions.toml=0.2.4`
- **AND** 后续 9 个 package job 不启动，不产生任何 release artifact

#### Scenario: workflow_dispatch dry-run

- **WHEN** 用户在 GitHub Actions UI 用 `workflow_dispatch` 触发，输入 version=0.1.0
- **AND** `versions.toml [project].version = "0.1.0"`
- **THEN** 9 个 package job 完整跑通
- **AND** publish job 用同样路径上传 release（dry-run 与正式无 publishing 分支差异 — 测试场景下通过手动删除 tag/release 清理）

#### Scenario: 非 semver tag 被忽略

- **WHEN** push 的 tag 是 `internal-build-2026` 或 `debug-v0.2.5`（不匹配 `v[0-9]+.[0-9]+.[0-9]+*`）
- **THEN** release.yml 不触发

### Requirement: Pre-release 自动标记

#### Scenario: Pre-1.0 版本

- **WHEN** tag 是 `v0.2.5`（version < 1.0.0）
- **THEN** `gh release create` 带 `--prerelease` 标志
- **AND** GitHub UI 不将该 release 显示在 "Latest release"

#### Scenario: RC tag

- **WHEN** tag 是 `v1.0.0-rc1`
- **THEN** 标 `--prerelease`（GitHub 默认按 `-` 后缀识别 prerelease，CI 显式再标一次保险）

#### Scenario: 正式 1.0+ release

- **WHEN** tag 是 `v1.0.0`（无 `-` 后缀且 ≥ 1.0.0）
- **THEN** 不带 `--prerelease`，显示为 "Latest release"

### Requirement: Artifact 命名 + 压缩格式

#### Scenario: 每个 RID 一个 archive

- **WHEN** release.yml 跑完
- **THEN** GitHub Release 页面包含以下 9 个文件（version 例 0.2.5；RID 名见 scripts/_lib/package_helpers.sh 白名单）：
  - `z42-0.2.5-linux-x64.tar.gz`
  - `z42-0.2.5-linux-arm64.tar.gz`
  - `z42-0.2.5-macos-arm64.tar.gz`
  - `z42-0.2.5-windows-x64.zip`
  - `z42-0.2.5-ios-arm64.tar.gz`
  - `z42-0.2.5-ios-arm64-sim.tar.gz`
  - `z42-0.2.5-android-arm64.tar.gz`
  - `z42-0.2.5-android-x64.tar.gz`
  - `z42-0.2.5-browser-wasm.tar.gz`
- **AND** Windows 包是 `.zip`，其余 8 个是 `.tar.gz`

#### Scenario: archive 内部结构

- **WHEN** 解压 `z42-0.2.5-macos-arm64.tar.gz`
- **THEN** 得到顶层目录 `z42-0.2.5-macos-arm64-release/`
- **AND** 该目录结构与 `scripts/package.sh release --rid osx-arm64` 本地产出一致（bin/ libs/ native/ examples/ manifest.toml ...）

### Requirement: SHA256SUMS 文件

#### Scenario: 与 archive 一同发布

- **WHEN** release 创建成功
- **THEN** Release 页面包含 `SHA256SUMS` 文件
- **AND** 文件内容为 9 行 coreutils 格式 `<sha256>  <filename>`
- **AND** 每行 filename 列对应一个上传的 archive
- **AND** 用户在本地执行 `sha256sum -c SHA256SUMS` 全部通过

### Requirement: `versions.toml [project].version` 作为版本 SoT

#### Scenario: 单一来源

- **WHEN** 开发者要 bump 版本号
- **THEN** 仅需修改 `versions.toml [project].version` 一处
- **AND** `src/runtime/Cargo.toml [workspace.package].version` 通过 drift-check 强制对账（manually mirrored）
- **AND** `src/runtime/Cargo.toml [package]` + 3 个 member Cargo.toml 通过 `version.workspace = true` 自动继承（无需手改）

#### Scenario: drift-check 捕获不一致

- **WHEN** `versions.toml [project].version = "0.2.5"`
- **AND** workspace root `Cargo.toml [workspace.package].version = "0.2.4"`
- **THEN** `scripts/check-versions-drift.sh` 退出码 1
- **AND** 输出形如 `✗ Cargo.toml [workspace.package].version  want=0.2.5 got=0.2.4`
- **AND** CI feature-matrix job fail

#### Scenario: package.sh 改读 versions.toml

- **WHEN** 本地执行 `scripts/package.sh release --rid macos-arm64`
- **THEN** 输出 `Package: z42-<v>-macos-arm64-release`，其中 `<v>` 等于 `versions.toml [project].version`
- **AND** 不再 grep `src/runtime/Cargo.toml` 取版本

## MODIFIED Requirements

### Requirement: scripts/check-versions-drift.sh 检查项

**Before**：6 类检查（Android minSdk/compileSdk / iOS deployment targets / wasm tool presence）。

**After**：7 类检查 — 在 Android 之前加入 `── project ──` 段，验证 `versions.toml [project].version` 与 `src/runtime/Cargo.toml [workspace.package].version` 一致。

### Requirement: scripts/package.sh VERSION 来源

**Before**：
```bash
VERSION=$(grep -E '^version' src/runtime/Cargo.toml | head -1 | sed -E 's/.*"([^"]+)".*/\1/')
[ -z "$VERSION" ] && VERSION="0.0.0"
```

**After**：
```bash
source "$SCRIPT_DIR/_lib/versions.sh"
VERSION=$(versions_get project.version)
[ -z "$VERSION" ] && { echo "error: versions.toml [project].version missing" >&2; exit 1; }
```

Fallback `"0.0.0"` 移除：若 SoT 缺字段，应该 fail-fast 而非静默用占位符。

## Pipeline Steps

本 spec 不涉及 z42 编译器 pipeline（lexer / parser / typecheck / codegen / VM）；只涉及 CI 工作流 + 工程脚本 + 工程文件结构（Cargo workspace）。
