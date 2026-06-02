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
| `core/` | **z42** | launcher 全部逻辑：argv 解析 / 子命令 / `~/.z42` 缓存 / 版本解析 / 起 app。编译为 `launcher.zpkg`（Exe-mode）。 |
| `src/` | Rust | 极小 trampoline `z42`：定位随装的 launcher 运行时 → `exec z42vm launcher.zpkg -- <argv>` → 回传退出码。（Phase 1，待落地） |

## 命令（P1）

`run` / `link` / `list` / `default` / `which` / `info`（本地、无网络）。
`install` / `uninstall` / `self update` / 下载 = P2（后续 spec）。

## 状态

进行中 —— spec：`docs/spec/changes/add-z42-launcher/`。
Phase 0（z42vm `-- args` 透传）已落地（commit fe0e0273）。
