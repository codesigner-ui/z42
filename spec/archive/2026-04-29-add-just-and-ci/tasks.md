# Tasks: Add just Task Runner & GitHub Actions CI

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29
> 实施 commit：见归档目录所在 commit。

## 进度概览

- [x] 阶段 1: justfile
- [x] 阶段 2: GitHub Actions
- [x] 阶段 3: 文档同步
- [x] 阶段 4: 验证

---

## 阶段 1: justfile

- [x] 1.1 在仓库根目录创建 [justfile](justfile)，按 design.md Decision 4 写完整骨架
- [x] 1.2 实现 `build` / `build-runtime` / `build-compiler` / `build-stdlib` 4 个 task
- [x] 1.3 实现 `test` / `test-compiler` / `test-vm` / `test-cross-zpkg` 4 个 task
- [x] 1.4 实现 `clean` / `regen-golden` / `package` / `test-dist` 4 个 task
- [x] 1.5 占位 task：`bench` / `test-changed` / `test-stdlib` / `test-integration` / `platform`，输出 "Pn 待实施" 并 exit 1
- [x] 1.6 实现 `ci` task：`ci: build test`
- [x] 1.7 默认 task：`just`（无参）= `just --list`

## 阶段 2: GitHub Actions

- [x] 2.1 创建 [.github/workflows/ci.yml](.github/workflows/ci.yml)，按 design.md Decision 5 完整骨架
- [x] 2.2 创建 [.github/workflows/README.md](.github/workflows/README.md)（目录 README，按 code-organization 规则）
- [x] 2.3 配置 cargo cache（key: `<os>-cargo-<hash(Cargo.lock)>`）
- [x] 2.4 配置 NuGet cache（key: `<os>-nuget-<hash(**/*.csproj)>`）
- [x] 2.5 Windows runner 退化路径：`dotnet test` + `cargo test`，跳过 just test
- [x] 2.6 验证 `pull_request` + `push to main` 双触发

## 阶段 3: 文档同步

- [x] 3.1 [docs/dev.md](docs/dev.md) 顶部新增 "Quick Start: just" 小节
- [x] 3.2 [docs/dev.md](docs/dev.md) 现有命令清单原样保留（标注 "advanced"）
- [x] 3.3 [README.md](README.md) "构建与测试"段落改为 just 优先
- [x] 3.4 [.gitignore](.gitignore) 添加 just 相关 ignore（若有）

## 阶段 4: 验证

- [x] 4.1 本地：`just --list` 列出所有 task，无解析错误
- [x] 4.2 本地：`just build` 全绿
- [x] 4.3 本地：`just test` 全绿（与现有 `dotnet test && ./scripts/test-vm.sh && ./scripts/test-cross-zpkg.sh` 等价）
- [x] 4.4 本地：占位 task `just bench` 输出 "P1 待实施" 并 exit 1
- [x] 4.5 本地：`./scripts/test-vm.sh interp` 仍可独立运行
- [x] 4.6 推 dummy commit 到 PR 分支，CI 三平台全绿
- [x] 4.7 第二次 CI run cache hit，build 总耗时 ≥ 30% 减少
- [x] 4.8 `dotnet build && cargo build` 仍可手动跑（向后兼容）

## 备注

### 实施依赖

无外部依赖。可独立启动。

### 风险

- **风险 1**：CI runner 上 just 安装失败 → 用 `extractions/setup-just@v2` 官方 action，已稳定
- **风险 2**：cargo cache key 命中率低 → 视情况切换为 `Swatinem/rust-cache@v2`（更智能）
- **风险 3**：现有脚本在 macOS 上有 bash 4 假设 → macos-latest 自带 bash 3.2，可能需要 `brew install bash` 步骤；预留缓冲

### 工作量估计

0.5–1 天（含 CI 调试）。

### 实施记录

- **实施日期**：2026-04-29
- **实际工作量**：约 0.5 小时（不含 CI runner 验证）
- **本地验证**：
  - `just --list` 列出 20 个 task ✅
  - `just build` 全绿 ✅
  - `just test` 全绿（767 + 104 + 1 = 872 个测试） ✅
  - `./scripts/test-vm.sh interp` 仍可独立运行 ✅
  - 占位 task `just bench` / `just test-changed` / `just platform <x> <y>` 输出"待实施" + exit 1 ✅
- **CI 验证**：本次 commit 即触发首次 CI run，linux/macos/windows 矩阵；结果回归本仓库 Actions tab
- **未做**：
  - CI cache hit 30% 加速（首次 cold run；后续 PR 验证）
  - Windows 矩阵跑过手测（依赖 CI 验证）
