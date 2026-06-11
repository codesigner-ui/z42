# runtime & workload 分发：安装 / 更新 / 运行

> ⚠️ **前瞻设计草案（未实施）**。把 GitHub Releases 作为分发后端，设计 launcher 对"不同版本 + 不同平台的 runtime + 相关 workload"的安装/更新/运行。现状 `z42 install <ver|nightly>`（[launcher.md](../runtime/launcher.md) `launcher-future-install` P2）是它的最小子集；本文是完整组织。落地开 spec。

## 现状（起点）

release.yml 发 9 个 RID 的 `z42-<ver>-<rid>.tar.gz` + `SHA256SUMS`，tag `v<ver>` / 滚动 `nightly`。`_cmdInstall` 只装 host RID，**靠约定拼资产名 + 读 SHA256SUMS**，无 manifest、无 channel/latest、无 workload、无 self-update，Windows `.zip` 路径未做。

## 关键区分：host runtime vs 目标平台产物（workload）

"不同平台的 runtime"是两类东西，别混：

| | **host runtime** | **目标平台产物（workload）** |
|---|---|---|
| 是什么 | 本机**跑 z42** 的运行时（z42vm + libs + launcher.zpkg + z42c）| 把 app **打包到别平台**的嵌入件（xcframework/AAR/wasm）+ 平台命令 |
| 一机几个 | 每版本一个（host RID）| 按需多个 |
| 命令 | `z42 install` | `z42 workload install`（版本作用域）|
| dotnet 对标 | 装 SDK | `dotnet workload install` |

9 RID 里 **4 桌面 = host runtime**；**ios/android/wasm = workload 产物**。

## 供给契约：每 release 发 manifest 资产（`release-index.json`）

不裸爬 GitHub API（rate-limit / 契约不稳 / 难离线 / 难签名）。每个 release 上传一个 `release-index.json` 资产作**稳定契约**（对标 rustup channel manifest / dotnet release-index）。channel 解析借 GitHub 现成的稳定 URL：

- **stable 最新** → `releases/latest/download/release-index.json`（GitHub `/latest/` 重定向到最新非 prerelease）
- **nightly** → `releases/download/nightly/release-index.json`（固定滚动 tag）
- **指定版本** → `releases/download/v<ver>/release-index.json`

### manifest schema（草案）

```json
{
  "schema": 1,
  "version": "0.3.5",
  "channel": "stable",
  "tag": "v0.3.5",
  "published": "2026-06-12T00:00:00Z",
  "runtimes": {
    "macos-arm64": { "archive": "z42-0.3.5-macos-arm64.tar.gz", "sha256": "…" },
    "linux-x64":   { "archive": "z42-0.3.5-linux-x64.tar.gz",   "sha256": "…" },
    "linux-arm64": { "archive": "…", "sha256": "…" },
    "windows-x64": { "archive": "z42-0.3.5-windows-x64.zip",    "sha256": "…" }
  },
  "workloads": {
    "ios":     { "archive": "z42-0.3.5-ios.tar.gz",     "sha256": "…", "host": ["macos-arm64"] },
    "android": { "archive": "z42-0.3.5-android.tar.gz", "sha256": "…", "host": ["macos-arm64","linux-x64","linux-arm64","windows-x64"] },
    "wasm":    { "archive": "z42-0.3.5-browser-wasm.tar.gz", "sha256": "…", "host": ["*"] }
  },
  "launcher": {
    "macos-arm64": { "archive": "z42-launcher-0.3.5-macos-arm64.tar.gz", "sha256": "…" }
  }
}
```

- `archive` 自带类型（`.tar.gz`/`.zip`）→ 统一解压逻辑，顺手解决 Windows `.zip`。
- `workloads.<wl>.host` = 哪些 host RID 能用该 workload（ios 仅 macOS）→ launcher 安装前先校验 host，host 不支持直接拒。
- `sha256` 内置 → 校验不再单独读 SHA256SUMS（SHA256SUMS 可作并行 legacy 资产保留）。

## `$Z42_HOME` 布局

```
~/.z42/
  bin/z42                  launcher 原生 trampoline（self update 目标）
  config.toml              default = "<ver>"  ·  channel = stable|nightly
  cache/                   下过的归档（校验通过再解压；断点/复用）
  runtimes/
    <ver>/                 ① host runtime（z42vm + libs + launcher.zpkg + z42c）
      workloads/<wl>/      ③ 版本作用域目标产物 + 平台命令（ABI 必须配套该 <ver>）
  tools/                   全局用户工具（类 ~/.cargo/bin，版本无关）
```

三类可安装物：**① runtime 版本（side-by-side）· ② launcher 自身 · ③ workload（挂某 runtime 版本下）**。

## 运行解析顺序（多版本选择）

```
--runtime <ver>                                显式最高
 > <app>.runtimeconfig.json  runtime.version   单 app pin
 > 项目 pin：z42.toml [project].runtime         rust-toolchain.toml 式（复用已有字段）
 > config.toml default
 > 唯一已装
 > 报错（提示 z42 install）
```

- 项目 pin **复用** 已有的 `z42.toml [project].runtime`（平台导出设计已用同字段），不引入新文件。值可为具体版本 `0.3.5`、范围 `0.3.x`、或 channel 名 `stable`/`nightly`。
- 现状只有 `--runtime > default > 唯一`；补 runtimeconfig pin + 项目 pin。

## channel + 更新

- **两个 channel**：`stable`（→ 最新 `v<ver>`）/ `nightly`（滚动）；存 `config.toml channel`。具体版号 pin 仍可用。
- `z42 install <ver|latest|nightly>` → 读对应 manifest 解析版本 → 下 host RID archive → 校验 sha → 解到 `runtimes/<ver>/`。
- `z42 update [--channel …]` → manifest 取 channel 最新 → 比已装新则装 +（可选）重指 default；nightly 用 version / `published` 比对（沿用 bootstrap 已有 published_at 逻辑）。
- `z42 self update` → 换 **trampoline 二进制**（`bin/z42`）：下 launcher archive → 原子替换（唯一须碰 native 自替换处）。注：launcher 逻辑在 `launcher.zpkg`（随 runtime 版本更新），trampoline 极薄、很少需自更。
- **workload ↔ runtime 版本联动**：workload 嵌入件 ABI 必须配 runtime 版本。升级 runtime → 提示/重装匹配 workload；`z42 workload update` 拉配套版本。

## 命令面（在现有 install/link/list/default/which/info 上扩）

```
z42 install <ver|latest|nightly>        # host runtime
z42 update [--channel stable|nightly]   # 升级 host runtime
z42 self update                         # 升级 launcher trampoline 自身
z42 default <ver>                       # 全局默认（已有）
z42 use <ver|stable|nightly>            # 项目 pin（写 z42.toml [project].runtime）
z42 list [--workloads]                  # 已装版本 + workload
z42 uninstall <ver>
z42 workload install <wl> [--runtime <ver>]
z42 workload list | update | remove <wl>
z42 run / z42 <app.zpkg>                # 解析版本 → 跑（已有）
```

`workload *` 与平台打包命令（`z42 publish ios` 等）由 workload 自身提供（见 [launcher-command-dispatch.md](launcher-command-dispatch.md)）；`install/update/self/default/use/list/uninstall` 属 launcher core（运行时自管理，baked-in）。

## 完整性 / 信任

- 现：`sha256`（内置 manifest）+ SHA256SUMS（legacy 并行）。
- 延后：manifest + archive 签名（GPG / sigstore）——与 roadmap `binary-package-signing` 延后一致。

## Decisions

| # | 决定 | 理由 |
|---|------|------|
| 1 | host runtime（`install`）与目标平台产物 workload（`workload install`）二分 | "不同平台"干净落位；对齐 dotnet SDK + workload |
| 2 | 每 release 发 `release-index.json` manifest 作供给契约 | 稳定/可缓存/可离线/可签名；避开裸 API 的 rate-limit + 契约不稳（rustup/dotnet 同选）|
| 3 | channel 解析借 GitHub `/latest/` 重定向 + 固定 `nightly` tag | 无需 API 枚举，stable URL 即解析入口 |
| 4 | 两 channel（stable/nightly）+ 项目 pin 复用 `z42.toml [project].runtime` | 最少概念；不引入新 version 文件 |
| 5 | workload 版本作用域（挂 `runtimes/<ver>/`）+ 与 runtime 版本联动 | 嵌入件 ABI 必须配 runtime |
| 6 | self update 只换薄 trampoline；launcher 逻辑随 runtime 走 | 自替换面最小化 |

## Deferred / 待 spec 细化

- manifest schema 定稿（多 archive 类型、可选 z42c-less 精简 runtime、依赖/兼容区间）。
- self update 的原子替换 + 回滚（Windows 文件占用、权限）。
- workload↔runtime 版本兼容矩阵与升级时的自动重装策略。
- 离线/镜像源（企业内网 mirror base URL）。
- 签名/校验链（manifest 签名 + archive 校验）。
- `z42 use` 范围 pin（`0.3.x`）的解析与自动安装策略。
