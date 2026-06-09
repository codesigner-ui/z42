# toolchain/launcher — `z42` launcher

## 职责

用户一次性安装的唯一入口 `z42`：解析所需运行时版本 → 用对应 `z42vm` 跑
Exe-zpkg → 透传命令行参数；并管理已装运行时（`~/.z42/runtimes/<ver>/`）。
类比 `dotnet` muxer + `rustup`。

**z42 优先**：只有"找/给 VM 的最小核"必须原生（bootstrap 铁律：无 VM 跑不了
z42）；其余逻辑全部用 z42 写。

## 结构

| 路径 | 语言 | 职责 |
|------|------|------|
| `core/launcher.z42` | **z42** | launcher 全部逻辑：argv 解析 / 子命令 / `~/.z42` 缓存 / 版本解析 / 起 app。编译为 `launcher.zpkg`（Exe-mode）。 |
| `core/apphost.z42` | **z42** | `z42 apphost build`：拷贝 apphost stub 模板 + patch 内嵌占位符 + macOS ad-hoc 重签名（add-apphost）。 |
| `src/lib.rs` | Rust | 共享 host-resolve/exec：`Runtime` / `z42_home` / `probe_runtime` / `resolve_*` / `exec_core`——trampoline 与 apphost 复用。 |
| `src/main.rs` | Rust | trampoline `z42`（installed→portable）：定位 launcher 运行时 → `exec z42vm launcher.zpkg -- <argv>` → 回传退出码。 |
| `src/apphost.rs` | Rust | 每-app 原生 apphost stub **模板**：内嵌占位符（`apphost build` patch 成 app zpkg 名）→ 运行时本地优先解析 → 起 app。占位符须 **volatile 读**（防 release const-fold）。 |

## 命令（P1）

`run` / `link` / `list` / `default` / `which` / `info` / `apphost build`（本地、无网络）。
`install` / `uninstall` / `self update` / 下载 = P2（后续 spec）。

`z42 apphost build <app.zpkg> [--out <name>]` → 产出 per-app 原生可执行文件（机制 / 本地优先解析 / 签名详见 [`docs/design/runtime/launcher.md`](../../../docs/design/runtime/launcher.md) 的 apphost 段）。

## 状态

进行中 —— spec：`docs/spec/changes/add-z42-launcher/`。
Phase 0（z42vm `-- args` 透传）已落地（commit fe0e0273）。
