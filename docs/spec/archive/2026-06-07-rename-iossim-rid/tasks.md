# Tasks: rename iOS simulator RID `ios-arm64-sim` → `iossim-arm64`

> 状态：🟢 已完成 | 创建：2026-06-07 | 类型：refactor（RID 命名约定，build/CLI）

**变更说明：** iOS 模拟器 RID 从 `ios-arm64-sim` 改为 `iossim-arm64`，使模拟器成为独立的 `iossim-` 平台前缀（与 device 的 `ios-` 并列），命名更统一。
**原因：** `ios-arm64-sim`（device 前缀 + `-sim` 后缀）不够统一；`iossim-arm64` 让"模拟器"成为一等平台前缀，且消除了 CI 后缀匹配里 "longer suffix wins" 的歧义顾虑。
**不改：** Rust target `aarch64-apple-ios-sim`（Apple 官方）、xcframework slice 名（Apple 定义）、`ios-x64-sim`（不支持，白名单外）。`docs/spec/archive/` 历史记录保持原名（不改写历史）。

- [x] 1.1 `scripts/xtask_package.z42`：`_ridCategory` 加 `iossim-` 前缀 → "ios" 类别（原仅 `ios-`）；`_ridToCargo` `ios-arm64-sim` → `iossim-arm64`（映射仍指向 `aarch64-apple-ios-sim`）
- [x] 1.2 `.github/workflows/release.yml`：matrix `rid: iossim-arm64`（rust-target 不变）
- [x] 1.3 `.github/workflows/ci.yml`：package 步骤 RID 列表 + nightly RID 后缀匹配（device/sim 现为不同前缀，去掉 "longer suffix wins" 注释）+ release-notes 表
- [x] 1.4 docs 同步：`docs/workflow/{building/ios,packaging,release,windows}.md`、`docs/design/runtime/{embedding,native-ext-loader}.md`、`examples/embedding/hello_c/README.md.ios`
- [x] 1.5 验证：`z42c build scripts/xtask.z42.toml` 0 error；`build package --rid iossim-arm64` → `category=ios` + `cargo aarch64-apple-ios-sim`（路由 smoke）

**文档影响：** 已同步上述 docs（RID 是 `--rid` CLI 参数 + 包名约定）。
