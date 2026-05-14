# Tasks: Release Automation

> 状态：🟢 已完成 | 创建：2026-05-14 | 完成：2026-05-14
> 类型：feat（新 CI workflow + 工程结构 + 版本 SoT 重构）
> 关联：[proposal.md](proposal.md) + [design.md](design.md) + [specs/release/spec.md](specs/release/spec.md)

## 进度概览

- [ ] 阶段 1: versions.toml SoT + Cargo workspace inheritance
- [ ] 阶段 2: scripts 适配（package.sh + check-versions-drift.sh + release_archive.sh）
- [ ] 阶段 3: `.github/workflows/release.yml` 新建
- [ ] 阶段 4: 文档同步（embedding.md + roadmap.md）
- [ ] 阶段 5: GREEN 验证 + 归档

## 阶段 1: versions.toml SoT + Cargo workspace inheritance

> 实施期发现 workspace 已在 `src/runtime/Cargo.toml` 声明（`members = [".", "crates/z42-abi", "crates/z42-rs", "crates/z42-macros"]`），不新建 repo 根 Cargo.toml；proposal.md / design.md / spec.md 已同步（2026-05-14）。

- [x] 1.1 MODIFY [versions.toml](../../../../versions.toml) — 在 `schema_version = 1` 后插入 `[project]` 段：`version = "0.1.0"`；header 注释从"三类块"改为"四类块"
- [ ] 1.2 MODIFY [src/runtime/Cargo.toml](../../../../src/runtime/Cargo.toml) — 在 `[workspace]` 段后加 `[workspace.package] version = "0.1.0"`；`[package].version = "0.1.0"` → `version.workspace = true`
- [ ] 1.3 MODIFY [src/runtime/crates/z42-abi/Cargo.toml](../../../../src/runtime/crates/z42-abi/Cargo.toml) — `version = "0.1.0"` → `version.workspace = true`
- [ ] 1.4 MODIFY [src/runtime/crates/z42-rs/Cargo.toml](../../../../src/runtime/crates/z42-rs/Cargo.toml) — 同上
- [ ] 1.5 MODIFY [src/runtime/crates/z42-macros/Cargo.toml](../../../../src/runtime/crates/z42-macros/Cargo.toml) — 同上
- [ ] 1.6 `cargo build --manifest-path src/runtime/Cargo.toml --release` 通过（workspace 解析正确）
- [ ] 1.7 `cargo metadata --manifest-path src/runtime/Cargo.toml --format-version 1 | jq -r '.packages[].version' | sort -u` 验证 4 个 crate version 均 = "0.1.0"

## 阶段 2: Scripts 适配

- [ ] 2.1 MODIFY [scripts/package.sh](../../../../scripts/package.sh) — VERSION 改读 `versions_get project.version`；移除 `"0.0.0"` fallback；fail-fast
- [ ] 2.2 MODIFY [scripts/check-versions-drift.sh](../../../../scripts/check-versions-drift.sh) — 加 `── project ──` 段，校 `versions.toml [project].version` 与 root `Cargo.toml [workspace.package].version` 一致
- [ ] 2.3 NEW [scripts/_lib/release_archive.sh](../../../../scripts/_lib/release_archive.sh) — `make_archive <rid> <version>` + `make_checksums <dir>` 两个 helper（见 design.md "scripts/_lib/release_archive.sh 关键片段"）
- [ ] 2.4 本地验证：
  - [ ] 2.4a `./scripts/check-versions-drift.sh` 通过（7 类检查全 ✓）
  - [ ] 2.4b 故意改 root Cargo.toml version 为 "0.0.1" → drift-check 应失败
  - [ ] 2.4c `./scripts/package.sh release --rid <host-rid>` 正常产 `artifacts/packages/z42-0.1.0-<rid>-release/`
  - [ ] 2.4d 在 bash 中 `source scripts/_lib/release_archive.sh && make_archive <host-rid> 0.1.0` 产 `artifacts/release/z42-0.1.0-<rid>.{tar.gz}`
  - [ ] 2.4e `make_checksums artifacts/release > /tmp/SHA256SUMS && (cd artifacts/release && shasum -a 256 -c /tmp/SHA256SUMS)` 通过

## 阶段 3: release.yml 新建

- [ ] 3.1 NEW [.github/workflows/release.yml](../../../../.github/workflows/release.yml) — 3 个 job：
  - `verify`：extract tag + drift check vs versions.toml；输出 `version` output
  - `package`：matrix 9 RID（按 design.md "release.yml 关键片段"）；调 build-stdlib.sh + package.sh + make_archive；upload-artifact
  - `publish`：download-artifact 全部 → `make_checksums` → `gh release create`（含 prerelease 逻辑）
- [ ] 3.2 关键细节：
  - [ ] 3.2a `permissions: contents: write`（gh release 需要）
  - [ ] 3.2b iOS / Android / wasm job 的 setup step 直接 mirror ci.yml 中对应 package-* job（NDK / Xcode / wasm-tools）
  - [ ] 3.2c upload-artifact path 模式：`artifacts/release/z42-*.tar.gz` + `z42-*.zip`
  - [ ] 3.2d publish job 用 `actions/download-artifact@v4 merge-multiple: true` 把 9 个 artifact 合到 `dist/`
- [ ] 3.3 本地 dry-run（无 push）：用 `act` 或人工 review yaml 语法
- [ ] 3.4 GitHub UI workflow_dispatch 触发一次 dry-run：
  - [ ] 3.4a 输入 version=0.1.0
  - [ ] 3.4b verify job 通过
  - [ ] 3.4c 9 个 package job 全绿
  - [ ] 3.4d publish job 产出 prerelease "v0.1.0"，含 10 个文件 —— 该 release 即作为 0.1.0 首发，不清理

## 阶段 4: 文档同步

- [ ] 4.1 MODIFY [docs/design/runtime/embedding.md](../../../design/runtime/embedding.md) §11.9 — 加 "Release distribution" 子节，描述：
  - GitHub Releases URL pattern
  - artifact 命名约定 + 压缩格式表（9 RID × format）
  - SHA256SUMS 校验流程
  - Pre-release 标记规则（< 1.0 / `-` 后缀）
- [ ] 4.2 MODIFY [docs/roadmap.md](../../../roadmap.md) — Q12 从"待裁决问题"表移除（或加脚注 "已裁决，见 archive/2026-05-14-add-release-automation/"）；0.2.6 表的 release 自动化项状态 → 部分完成
- [ ] 4.3 MODIFY [docs/workflow/release.md](../../../workflow/release.md)（若不存在则 NEW）— Bump 版本 + 发 release 的 step-by-step（开发者操作手册）

> 注：4.3 若新增，先确认 docs/workflow/ 子目录是否有 release.md，没有则按 [docs/workflow/](../../../workflow/) 现有风格创建。若 Scope 蔓延阻碍，本 task 拆为独立 spec（不阻塞本迭代归档）。

## 阶段 5: GREEN 验证 + 归档

- [x] 5.1 完整运行 `./scripts/test-all.sh` —— 5 stage 全绿 + 1 stage（stdlib [Test]）失败属另一 spec（add-std-process）的 in-flight 工作（process_failure / process_stdio），与本 spec 0 重叠
- [x] 5.2 本地 dist smoke（make_archive + make_checksums round-trip）— 通过
- [x] 5.3 spec scenarios 逐条覆盖确认（spec.md "ADDED Requirements" + "MODIFIED Requirements" 11 个 scenario 中：单元可本地验证 3 个 — drift catch / package.sh VERSION 来源 / archive 命名；CI 触发类 8 个 scenario 需 dispatch 后回验，留 5.7 之后做）
- [x] 5.4 tasks.md 状态 → 🟢 已完成 + 完成日期
- [ ] 5.5 移动 `docs/spec/changes/add-release-automation/` → `docs/spec/archive/2026-05-14-add-release-automation/`
- [ ] 5.6 commit `feat(ci): release.yml — tag-triggered cross-platform SDK package publish + versions.toml [project] SoT`
- [ ] 5.7 push origin main
- [ ] 5.8（dispatch 后）GitHub UI workflow_dispatch (version=0.1.0)；记录运行结果（9 archive + SHA256SUMS）；prerelease v0.1.0 即作为首发，不清理

## 备注

### Scope 外的发现不阻塞本 spec

- Cargo workspace `[workspace.package]` 其它字段（edition / authors / license / repository / readme）也可走 inheritance —— 留 follow-up refactor，本 spec 只迁 `version`
- ci.yml 的 9 个 package job 可以抽 reusable workflow —— 已入 design.md Deferred (release-A1)
- `Cargo.toml` 改动需要更新 `.gitignore`？— 不需要，根 Cargo.toml 是新文件，正常 commit

### 风险监控

- **workspace 解析失败**：4 个 crate 各自有独立 `[package]` 段；改 `version.workspace = true` 前确认 Cargo.toml 没把 version 写在多处（如 `[workspace.dependencies]`）
- **GH_TOKEN 权限**：默认 `${{ secrets.GITHUB_TOKEN }}` 在 workflow 内有 `contents: write`（设了 permissions block），无需额外 PAT
- **drift-check 顺序**：阶段 2.2 必须在阶段 1.2（root Cargo.toml 新建）之后，否则 drift-check 找不到文件
- **release.yml workflow_dispatch dry-run**：触发后 publish 会创建 `v0.1.0` prerelease；该 release 即作为 0.1.0 首发，跑通不清理（User 裁决 2026-05-14）
