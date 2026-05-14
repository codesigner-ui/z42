# Design: Release 自动化（tag → cross-platform SDK packages）

## Architecture

```
┌────────────────────────────────────────────────────────────────────┐
│  Developer                                                         │
│    1. bump versions.toml [project].version = "0.2.5"               │
│    2. commit + push                                                │
│    3. git tag v0.2.5 && git push --tags                            │
└──────────────────────────────┬─────────────────────────────────────┘
                               │ push: tags: 'v[0-9]+.[0-9]+.[0-9]+*'
                               ▼
┌────────────────────────────────────────────────────────────────────┐
│  .github/workflows/release.yml                                     │
│                                                                    │
│  Job 1: verify-tag-matches-version    (ubuntu-latest, 1 min)       │
│    ├─ checkout                                                     │
│    ├─ extract tag = ${GITHUB_REF#refs/tags/v}                      │
│    ├─ read versions.toml [project].version via scripts/_lib/...    │
│    └─ assert equal → fail-fast if drift                            │
│                                                                    │
│  Job 2..10: package-<rid>             (matrix; 9 jobs in parallel) │
│    runs-on: <platform host>                                        │
│    needs: verify-tag-matches-version                               │
│    steps:                                                          │
│      ├─ checkout                                                   │
│      ├─ setup .NET / Rust / NDK / Xcode / wasm-tools (per RID)     │
│      ├─ build-stdlib.sh (produces z42c.dll + .zpkg)                │
│      ├─ scripts/package.sh release --rid <rid>                     │
│      ├─ release_archive.sh make_archive <pkg_dir>                  │
│      │       → artifacts/release/z42-<v>-<rid>.{tar.gz|zip}        │
│      └─ upload-artifact (intermediate, scoped to this run)         │
│                                                                    │
│  Job 11: publish                      (ubuntu-latest, ~1 min)      │
│    needs: [all 9 package-<rid> jobs]                               │
│    steps:                                                          │
│      ├─ download-artifact (all 9 archives)                         │
│      ├─ release_archive.sh make_checksums → SHA256SUMS             │
│      ├─ gh release create v<version>                               │
│      │     --generate-notes                                        │
│      │     --prerelease (if version < 1.0 OR tag contains '-')     │
│      │     z42-<v>-<9 rids>.{tar.gz|zip} SHA256SUMS                │
│      └─ done — release visible on GitHub                           │
└────────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 版本号 SoT —— versions.toml `[project].version`

**问题**：当前 4 个 Cargo.toml 各写 `version = "0.1.0"`，外加 `package.sh`
直接 grep `src/runtime/Cargo.toml`，**没有单一真相源**，未来 release 流程必然出现
"tag = v0.2.5 但 Cargo.toml 还停在 0.1.0"。

**选项：**
- A — `versions.toml [project].version` 作 SoT，4 个 Cargo.toml 通过 workspace
  inheritance 引用，`package.sh` 改读 versions.toml
- B — 沿用 `src/runtime/Cargo.toml` 作 SoT，引入 build script 模板 Cargo.toml
- C — tag 作 SoT，CI 运行时 patch Cargo.toml + versions.toml

**决定**：选 A。

**理由：**
- 与 versions.toml 现有 toolchain / build / platform 三类 SoT 一脉相承（"单一文件就是答案"）
- workspace.package.version inheritance 是 Cargo 1.64+ 原生能力，零脚本魔法
- B 引入额外 build step（侵入 cargo 流程）违反 versions.toml 现有 "verify-only" 策略
- C 让 tag 之外的开发动作（本地 `cargo build --release`）行为不可预测

**实现细节：**
- Cargo workspace 已存在于 `src/runtime/Cargo.toml`（`[workspace] members = [".", "crates/z42-abi", "crates/z42-rs", "crates/z42-macros"]`），不新建 repo 根 Cargo.toml
- 在 `src/runtime/Cargo.toml` 加 `[workspace.package]` 段：
  ```toml
  [workspace.package]
  version = "0.1.0"   # ⚠️ mirror of versions.toml [project].version (drift-check)
  ```
- `src/runtime/Cargo.toml [package]` + 3 个 member Cargo.toml `[package]` 全部改用 `version.workspace = true`
- drift-check：`versions.toml [project].version` 必须 == `src/runtime/Cargo.toml [workspace.package].version`

### Decision 2: Tag 触发 trigger pattern

**问题**：tag `v0.2.5` / `v0.2.5-rc1` / `v0.2.5-rc.1` / `prod-2026` 应不应触发？

**选项：**
- A — `v[0-9]+.[0-9]+.[0-9]+*`（严格 semver 前缀；支持 `-rc1` / `-rc.1` / `-beta` 后缀）
- B — `v*`（任何 v-prefix tag）
- C — 不限 pattern，全部触发

**决定**：选 A。

**理由：**
- 严格 pattern 防止非 release tag（如 `v-internal-debug`）误触发
- 支持 pre-release 后缀（GitHub 自动识别带 `-` 的 tag 为 prerelease，无需额外字段）
- A 是 Rust / Go / Node 等主流项目的 release tag 默认约定

**实现：**
```yaml
on:
  push:
    tags: ['v[0-9]+.[0-9]+.[0-9]+*']
  workflow_dispatch:
    inputs:
      version:
        description: 'Version (e.g. 0.2.5) — used only for dry-run; real releases must come from tag push'
        required: true
        type: string
```

### Decision 3: Artifact 命名 + 压缩格式

**问题**：tarball 命名是 `z42-<v>-<rid>-release.tar.gz` 还是 `z42-<v>-<rid>.tar.gz`？
Windows 用 zip 还是 tar.gz？

**决定**：

- **命名**：`z42-<version>-<rid>.{ext}`（drop `-release` 后缀）
  - 例：`z42-0.2.5-macos-arm64.tar.gz`
  - 理由：pre-1.0 release pipeline 永远 `release` profile，后缀冗余；与 PKG_NAME
    `z42-<v>-<rid>-release` 的目录名只差 `-release`，CI 步骤简单 strip
- **压缩格式**：`windows-x64` → `.zip`；其它 8 RID → `.tar.gz`
  - 理由：Windows 用户默认无 tar/gunzip（PowerShell 5.x），zip 是原生交付物；
    Unix 平台 tar.gz 是事实标准

### Decision 4: Pre-release 标记

**问题**：pre-1.0 的所有 release 是否应该标 `prerelease = true`？

**决定**：

- **Pre-1.0**（version < `1.0.0`）：自动 `--prerelease`（GitHub 不在 "Latest release" 高亮）
- **Tag 含 `-`**（如 `v0.2.5-rc1`）：自动 `--prerelease`（GitHub 默认行为，也再强制一次）
- 1.0+ 且无 `-` 后缀：正式 release

**实现**：
```bash
prerelease_flag=""
if [[ "$VERSION" == *-* ]] || version_lt "$VERSION" "1.0.0"; then
  prerelease_flag="--prerelease"
fi
gh release create "v$VERSION" $prerelease_flag --generate-notes "$@"
```

`version_lt` 用简单的 sort -V 实现：
```bash
version_lt() {
  [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1)" = "$1" ]
}
```

### Decision 5: Release notes 生成

**问题**：自动生成 release notes 还是要求 user 手填？

**决定**：`gh release create --generate-notes`（GitHub 内置）。

**理由：**
- 自动从 PR / commit 抽取（commit message 风格已在 CLAUDE.md 约束为 `type(scope): 描述`）
- 不引入额外依赖（git-cliff / conventional-changelog 是 npm 生态）
- pre-1.0 节奏快，手写 release notes 是 friction；自动版可用就用，将来想覆写也容易（GitHub UI 编辑）

### Decision 6: SHA256SUMS 文件

**问题**：是否随包发布校验和？

**决定**：发布 1 个 `SHA256SUMS` 文件（10 行 = 9 包 + 文件自身签名留空），coreutils 风格。

**格式**：
```
abc...123  z42-0.2.5-macos-arm64.tar.gz
def...456  z42-0.2.5-linux-x64.tar.gz
...
```

**理由：**
- 下游用户 / 包管理器（homebrew formula / aur PKGBUILD）需要校验和
- Signing 留 backlog（`binary-package-signing`），SHA256 是低成本中间方案
- 单文件简单（不必每个 tar.gz 配 `.sha256`），coreutils `sha256sum -c SHA256SUMS` 一键校验

### Decision 7: Job 编排策略 —— 共用 ci.yml 还是独立 release.yml

**问题**：是否抽取 ci.yml 现有 9 个 package job 共享逻辑（reusable workflow），
让 release.yml 调用？

**选项：**
- A — release.yml 独立写，重复 9 个 package job 的逻辑
- B — 把 9 个 package job 抽成 `.github/workflows/_package-matrix.yml`
  （reusable workflow），ci.yml 和 release.yml 各自 `workflow_call`
- C — release.yml 内联 9 个 job，但用 yaml anchor / composite action 减少重复

**决定**：选 A（v0），B 留 follow-up。

**理由：**
- 当前优先级是把 release pipeline 跑通，不是复用代码
- ci.yml 的 package job 主要做 **smoke verify**（lipo / file / wasm-tools 等），
  release 阶段不需要 verify（CI 已 verify 过），只需 build + archive；二者不是 1:1 复用
- composite action / reusable workflow 引入额外 YAML 跳转层，第一版优先可读
- 抽 reusable workflow 是独立 refactor 议题，后续要做可单开 spec

**风险监控：** ci.yml 和 release.yml 出现 build step 漂移 → 短期靠 review + smoke verify
工具链一致；长期由 follow-up reusable workflow 解决。

### Decision 8: 1.0 前 vs 1.0 后版本号 bump 流程

**问题**：版本号谁 bump？什么时候 bump？

**决定**：人工 bump（pre-1.0 阶段足够）。流程：

1. PR / commit 改 `versions.toml [project].version` + drift-check 通过
2. 合到 main 后，`git tag v<new-version>` + `git push --tags`
3. release.yml 触发

**延后**：1.0+ 引入 `cargo-release` 类工具自动化 bump + tag + push 一气呵成。
入 design doc Deferred 段（参见下方 Deferred）。

## Implementation Notes

### release.yml 关键片段

```yaml
name: Release

on:
  push:
    tags: ['v[0-9]+.[0-9]+.[0-9]+*']
  workflow_dispatch:
    inputs:
      version:
        description: 'Dry-run version (e.g. 0.2.5)'
        required: true

permissions:
  contents: write   # gh release create

jobs:
  verify:
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.extract.outputs.version }}
    steps:
      - uses: actions/checkout@v4
      - id: extract
        run: |
          if [ "$GITHUB_EVENT_NAME" = "push" ]; then
            tag="${GITHUB_REF#refs/tags/v}"
          else
            tag="${{ inputs.version }}"
          fi
          source scripts/_lib/versions.sh
          repo_version=$(versions_get project.version)
          [ "$tag" = "$repo_version" ] \
            || { echo "drift: tag=$tag versions.toml=$repo_version" >&2; exit 1; }
          echo "version=$tag" >> "$GITHUB_OUTPUT"

  package:
    needs: verify
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: linux-x64,      runs-on: ubuntu-latest }
          - { rid: linux-arm64,    runs-on: ubuntu-24.04-arm }
          - { rid: macos-arm64,    runs-on: macos-15 }
          - { rid: windows-x64,    runs-on: windows-latest }
          - { rid: ios-arm64,      runs-on: macos-15 }
          - { rid: ios-arm64-sim,  runs-on: macos-15 }
          - { rid: android-arm64,  runs-on: ubuntu-latest }
          - { rid: android-x64,    runs-on: ubuntu-latest }
          - { rid: browser-wasm,   runs-on: ubuntu-latest }
    runs-on: ${{ matrix.runs-on }}
    steps:
      - uses: actions/checkout@v4
      - # setup .NET / Rust / target / NDK / wasm-tools (per RID; mirror ci.yml package-* jobs)
      - run: ./scripts/build-stdlib.sh
      - run: ./scripts/package.sh release --rid ${{ matrix.rid }}
      - name: Archive
        shell: bash
        run: |
          source scripts/_lib/release_archive.sh
          make_archive "${{ matrix.rid }}" "${{ needs.verify.outputs.version }}"
      - uses: actions/upload-artifact@v4
        with:
          name: release-${{ matrix.rid }}
          path: artifacts/release/z42-*.{tar.gz,zip}
          if-no-files-found: error

  publish:
    needs: [verify, package]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/download-artifact@v4
        with: { path: dist, pattern: 'release-*', merge-multiple: true }
      - name: Generate SHA256SUMS
        run: |
          source scripts/_lib/release_archive.sh
          make_checksums dist > dist/SHA256SUMS
      - name: Create release
        env: { GH_TOKEN: ${{ secrets.GITHUB_TOKEN }} }
        run: |
          v="${{ needs.verify.outputs.version }}"
          prerelease=""
          if [[ "$v" == *-* ]] || \
             [ "$(printf '%s\n1.0.0\n' "$v" | sort -V | head -1)" = "$v" ] \
             && [ "$v" != "1.0.0" ]; then
            prerelease="--prerelease"
          fi
          gh release create "v$v" $prerelease --generate-notes dist/*
```

### scripts/_lib/release_archive.sh 关键片段

```bash
# make_archive <rid> <version>
# Reads artifacts/packages/z42-<version>-<rid>-release/ → writes artifacts/release/z42-<version>-<rid>.<ext>
make_archive() {
    local rid="$1" version="$2"
    local pkg_dir="artifacts/packages/z42-${version}-${rid}-release"
    local out_dir="artifacts/release"
    mkdir -p "$out_dir"
    local ext="tar.gz"
    [[ "$rid" == win-* ]] && ext="zip"
    local archive="$out_dir/z42-${version}-${rid}.${ext}"
    if [ "$ext" = "zip" ]; then
        (cd "$(dirname "$pkg_dir")" && zip -rq "$OLDPWD/$archive" "$(basename "$pkg_dir")")
    else
        tar -C "$(dirname "$pkg_dir")" -czf "$archive" "$(basename "$pkg_dir")"
    fi
    echo "✓ $archive"
}

# make_checksums <dir>
# Prints SHA256SUMS-format lines for *.tar.gz / *.zip in <dir>
make_checksums() {
    local dir="$1"
    (cd "$dir" && shasum -a 256 z42-*.tar.gz z42-*.zip 2>/dev/null | sort) || true
}
```

### versions.toml 改动

```toml
schema_version = 1

# ══════════════════════════════════════════════════════════════════════════
# PROJECT: z42 自身的版本（release tag 必须与此一致）
# ══════════════════════════════════════════════════════════════════════════

[project]
version = "0.1.0"                       # → src/runtime/Cargo.toml [workspace.package].version
# Bumping: 改本字段 → drift-check 提示 Cargo.toml 更新 → commit → git tag v<version>
```

放在 `schema_version = 1` 后面、`[toolchain.*]` 之前；header 注释更新提及"四类块"（project / toolchain / build / platform）。

### check-versions-drift.sh 新增检查

```bash
# ── project version ──────────────────────────────────────────────────────
echo "── project ───────────────────────────────────────────────────────────────"
RUNTIME_CARGO="$ROOT/src/runtime/Cargo.toml"
want=$(versions_get project.version)
# 锁定 [workspace.package] 段下的 version，避免误命中 [package] / [dependencies]
got=$(awk '
    /^\[workspace\.package\]/ { in_section = 1; next }
    /^\[/                     { in_section = 0 }
    in_section && /^[[:space:]]*version[[:space:]]*=/ {
        gsub(/.*"|".*/, ""); print; exit
    }' "$RUNTIME_CARGO")
check "src/runtime/Cargo.toml [workspace.package].version" "$want" "$got"
```

workspace inheritance 保证 `src/runtime/Cargo.toml [package]` + 3 个子 crate 自动继承，drift-check 只查 `[workspace.package]` 段。

## Testing Strategy

| 层次 | 验证方式 |
|------|---------|
| **本地脚本单测** | `scripts/check-versions-drift.sh` 跑通；故意改动 `src/runtime/Cargo.toml [workspace.package].version` 看是否报 drift |
| **本地 release_archive** | `make_archive macos-arm64 0.1.0` 跑通 + `tar -tzf` 看包结构正确；`make_checksums dist` 输出 SHA256SUMS 格式 |
| **CI dry-run** | 用 `workflow_dispatch` 在 fork / branch 跑一次，确认 9 个 archive 产出 + publish job 顺利上传到一个 *test* prerelease，然后删除 |
| **正式 tag 发布** | `git tag v0.1.0` push 触发完整 release pipeline；下游手动验证下载链接 + sha256 一致 |

GREEN 标准与 workflow.md 阶段 8 相同（`test-all.sh` 全绿）。

## Deferred / Future Work

### release-A1: Reusable workflow（ci.yml ↔ release.yml 共享 9 package job）

- **来源**：本 spec design decision 7
- **触发原因**：v0 优先把 release 流程跑通；reusable workflow 抽象引入额外 YAML 层不利于第一版可读
- **前置依赖**：release.yml 跑稳几个版本后再评估冗余度
- **触发条件**：ci.yml 与 release.yml 的 build step 出现 ≥3 次漂移修复 / 维护成本可见上升

### release-A2: cargo-release 自动 bump + tag + push

- **来源**：本 spec design decision 8
- **触发原因**：pre-1.0 手工 bump 足够，自动化收益不显著
- **前置依赖**：CHANGELOG.md 规范 + 1.0 SemVer 启用决策
- **触发条件**：1.0-rc / 1.0 正式发布周期

### release-A3: Notarization / 代码签名

- **来源**：proposal Out of Scope
- **状态**：已在 [embedding.md §11.9](../../../design/runtime/embedding.md#119-分发-package-形态per-arch-flat2026-05-13-define-package-layout) Deferred 段记录为 `binary-package-signing`，本 spec 不重复登记

### release-A4: 多渠道分发（Homebrew / apt / winget / npm）

- **来源**：proposal Out of Scope
- **触发原因**：1.0 之前没有稳定承诺，包管理器频繁 bump 无意义
- **前置依赖**：1.0 SemVer / deprecation 启用
- **触发条件**：1.0 release 后的下游集成 spec
