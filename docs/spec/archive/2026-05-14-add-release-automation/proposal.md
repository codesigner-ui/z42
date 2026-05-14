# Proposal: Release 自动化（tag → cross-platform SDK packages on GitHub Releases）

## Why

z42 已具备 9 个 RID 的 SDK packaging job（4 desktop + 2 iOS + 2 Android + 1 wasm），
但产物只 upload 为 GitHub Actions artifact（保留期 ≤90 天，需登录下载）。
没有 tag-triggered release pipeline = 不能向外部用户分发二进制 = 阻塞 roadmap **0.2.6**
退出标准（"Release 自动化：git tag → 跨平台 z42c/z42vm 二进制 + zpkg 自动产出"）。

同时引入 `versions.toml [project].version` 作为项目版本 SoT，消除 4 个 Cargo.toml
里 `version = "0.1.0"` 重复声明的漂移风险（与 versions.toml 现有的 toolchain/build/
platform 三类 SoT 一脉相承）。

## What Changes

- **NEW** `.github/workflows/release.yml`：tag push (`v*.*.* `) 或 `workflow_dispatch` 触发
- **NEW** `[project]` 段加入 `versions.toml`，承载 `version = "<semver>"`
- **MODIFY** `src/runtime/Cargo.toml` 加 `[workspace.package].version`（workspace 已存在于此，非 repo 根），自身 `[package].version` 改为 `version.workspace = true`
- **MODIFY** 3 个 member Cargo.toml 改用 `version.workspace = true` 继承
- **MODIFY** `scripts/package.sh` 改从 `versions.toml` 读 version（替代 `Cargo.toml` grep）
- **MODIFY** `scripts/check-versions-drift.sh` 加 project version drift 检查
- **NEW** `scripts/_lib/release_archive.sh` helper：tar.gz/zip 打包 + SHA256SUMS 生成
- Q12 裁决落地：artifact 命名 `z42-<version>-<rid>.{tar.gz|zip}`

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `.github/workflows/release.yml`                            | NEW    | Tag-triggered release pipeline（9 RID matrix） |
| `versions.toml`                                            | MODIFY | 新增 `[project].version` 段；header 注释更新（4 类块）|
| `src/runtime/Cargo.toml`                                   | MODIFY | 加 `[workspace.package].version`（workspace 已在此声明）；`[package].version` → `version.workspace = true` |
| `src/runtime/crates/z42-abi/Cargo.toml`                    | MODIFY | `version = "0.1.0"` → `version.workspace = true` |
| `src/runtime/crates/z42-rs/Cargo.toml`                     | MODIFY | 同上 |
| `src/runtime/crates/z42-macros/Cargo.toml`                 | MODIFY | 同上 |
| `scripts/package.sh`                                       | MODIFY | VERSION 改读 `versions_get project.version` |
| `scripts/check-versions-drift.sh`                          | MODIFY | 加 project version 段（校 versions.toml 与 `src/runtime/Cargo.toml [workspace.package].version` 一致）|
| `scripts/_lib/release_archive.sh`                          | NEW    | `make_archive` / `make_checksums` helper |
| `docs/design/runtime/embedding.md`                         | MODIFY | §11.9 加 "release distribution" 子节（GitHub Releases 链接 + 命名约定）|
| `docs/roadmap.md`                                          | MODIFY | Q12 移出"待裁决"列表并写入"已裁决决策"或脚注；0.2.6 进度更新 |

**只读引用**（理解上下文必须读，不修改）：

- `.github/workflows/ci.yml` — 现有 9 个 package job 的 build/verify 步骤格式参考
- `scripts/_lib/versions.sh` — `versions_get` API 已有，release.yml 沿用
- `scripts/_lib/package_*.sh` — 包内容生成 helper（release.yml 不直接调，通过 package.sh 间接调）

## Out of Scope

- **Notarization / 代码签名**（iOS xcframework / Android AAR / Windows）— 留 backlog `binary-package-signing`
- **多平台 source tarball**（`z42-<v>-src.tar.gz` 包含完整源码）— v0 不发，需要时独立 spec
- **npm publish wasm package** — wasm SDK 暂以 tar.gz 形式发，npm 入口属后续
- **Docker image / Homebrew tap / apt repo / winget** — 发行渠道下游集成，独立 spec
- **z42c new 模板里的 CI 模板更新** — 属 0.2.5 工作流，本 spec 不动
- **Cargo workspace 全量重构**（其它共享字段如 edition / authors / license 也走 workspace.package）— 本 spec 只迁 `version`，避免范围蔓延
- **Pre-release suffix 自动化**（`v0.2.5-rc1` 自动 prerelease=true）— 走 GitHub 默认行为（tag 含 `-` → prerelease），不写额外逻辑

## Open Questions

无（D1–D8 已与 User 在阶段 1 探索中对齐）。
