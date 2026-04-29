# GitHub Actions Workflows

## 职责

仓库的 CI/CD 自动化配置。每个 `.yml` 文件定义一个独立 workflow。

## 当前 workflows

| 文件 | 触发条件 | 职责 |
|------|---------|------|
| `ci.yml` | `pull_request` 到 main / `push` 到 main | 在 linux-x64 / macos-aarch64 / windows-x64 三平台跑 `just build` + `just test`（Windows 退化为 smoke test） |

## 设计约定

- **统一入口**：所有 workflow 通过 `just <task>` 调用，避免在 yaml 里硬编码命令
- **缓存策略**：cargo（按 `Cargo.lock` 哈希）+ NuGet（按 `**/*.csproj` 哈希）
- **并发控制**：同一 ref 的旧 run 自动取消（`concurrency` 段）
- **Windows 退化**：现有 `scripts/*.sh` 是 bash 脚本；Windows runner 不跑 `just test`，改为直接 `dotnet test` + `cargo test`

## 后续 workflows（占位）

- P1: `bench-update.yml` —— push to main 时全量 benchmark + 上传 baseline
- P4.2: `platform-wasm` job 加入 ci.yml
- P4.3: `platform-android` job
- P4.4: `platform-ios` job

详见 [spec/changes/](../../spec/changes/) 中各 sub-spec 的 CI 接入设计。
