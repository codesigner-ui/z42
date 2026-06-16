# Tasks: add-desktop-export (B 第一步) — 🟢 已完成

**变更说明：** apphost-as-config 落地——加 `z42 export desktop`（读 `[platform.desktop]`，产 apphost），取消 `z42 apphost` 命令；apphost.z42 stub-patch 逻辑成为 desktop export 实现。
**原因：** apphost 是平台配置（`[platform.desktop]`）的发布产物，与 ios/android/wasm export 对称（User 裁决 2026-06-17）。
**锁：** `toolchain` + `compiler`（跨锁，均空闲）。

- [x] 1.1 apphost.z42：删 `BuildOne`/`_usage`/`_tomlStr`/`_joinProj`；保留 patch 原语；加公开 `Produce(app, outPath)`
- [x] 1.2 launcher_export.z42：加 `_cmdExportDesktop(r)`（读 `[platform.desktop].publish_dir` + `_expResolveZpkg` + `Apphost.Produce`）
- [x] 1.3 launcher_cli.z42：删 apphost router/dispatch；export router 加 `desktop`
- [x] 1.4 xtask.z42.toml：`[apphost]`→`[platform.desktop]`
- [x] 1.5 xtask_test_dist.z42：smoke appToml 加 name+`[platform.desktop]` + 调用改 `z42 export desktop`
- [x] 1.6 **compiler（跨锁）**：ProjectManifest WS008 注册 `[platform.desktop].publish_dir` + 删退役 `[apphost]` 已知段；2 个 WS008 测试改 desktop
- [x] 1.7 docs：launcher.md（apphost 段→[platform.desktop]/export desktop）/ launcher README / project.md（`[platform.desktop]` schema）/ export.md（desktop 行）/ building+testing workflow / 各代码注释
- [x] 1.8 GREEN：
  - dotnet build + ProjectManifest 43/43（含 2 新 desktop WS008）
  - launcher + xtask 清缓存重编 exit 0、零 WS008
  - **运行级**：`z42 export desktop scripts/xtask.z42.toml` → ./xtask 产出且 `./xtask --help` 跑通（载重门通过）
- [x] 1.9 COMMIT + 归档，释放 toolchain + compiler 锁

## 备注
- crate `Apphost` 保留为内部 patch 库（`PatchBytes` 仍 public 可单测）。
- ./xtask gitignore，重生不提交；本地实跑用 debug z42vm+stub 验证后已恢复原 release ./xtask。
- apphost shorthand `z42 app.zpkg`（与 apphost 子命令无关）不受影响。
