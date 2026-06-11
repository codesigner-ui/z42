# runtime & workload 分发：安装 / 更新 / 运行

> ⚠️ **前瞻设计草案（未实施）**。把 GitHub Releases 作为分发后端，设计 launcher 对"不同版本 + 不同平台的 runtime + 相关 workload"的安装/更新/运行。现状 `z42 install <ver|nightly>`（[launcher.md](../runtime/launcher.md) `launcher-future-install` P2）是它的最小子集；本文是完整组织。落地开 spec。

## 现状（起点）

release.yml 发 9 个 RID 的 `z42-<ver>-<rid>.tar.gz` + `SHA256SUMS`，tag `v<ver>` / 滚动 `nightly`。`_cmdInstall` 只装 host RID，**靠约定拼资产名 + 读 SHA256SUMS**，无 manifest、无 channel/latest、无 workload、无 self-update，Windows `.zip` 路径未做。

## runtime / workload：名词 vs 命令 vs 组件（消除"两处都含 runtime"的冗余）

**名词 `runtime`（per-RID）**：某 RID 的 z42vm/嵌入件——host（macos/linux/windows）与 target（ios/android/wasm）**都是 runtime**，只是 RID 不同。manifest 里它们都是 `runtimes` 段的**组件（pack）**。

**但用户面只有两个安装入口，target runtime 不开第二条直装路径**：

| 入口 | 装什么 | 说明 |
|---|---|---|
| `z42 install <ver>` | **host runtime**（z42vm + libs + z42c）| 必备、能跑 z42 的前提；**只此一处直装 runtime** |
| `z42 workload install <plat>` | **能力束** = 组合 {target runtime pack} + {打包工具 + 模板 + native glue} | 拿到 ios/android/wasm runtime 的**唯一**用户面路径 |

→ target runtime 是 workload 内部组合的 **component**，不作为独立 install 命令暴露 ⇒ **无冗余、无"两条路装同一个 runtime"的困惑**。对标 dotnet：你 `dotnet workload install ios`，ios runtime pack 是 workload 内的 component，从不 `dotnet install ios-runtime`。

9 RID：4 桌面 RID 的 runtime 经 `z42 install`；ios/android/wasm 的 runtime pack 经对应 workload 拉入。

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

## launcher / runtime 拆分 + 首次 bootstrap

**拆分**：launcher 与 runtime 不再同包、各自独立更新。
- **launcher bundle** = 原生 trampoline + `launcher.zpkg`，**不带 vm**；trampoline 解析已装 runtime → 用**它的** z42vm 跑 launcher.zpkg。
  > 现状 launcher 自带一个 z42vm（`$Z42_HOME/launcher/z42vm`）；本设计改为**复用已装 runtime 的 vm** —— 既不必为 launcher 造/最小化 vm，NativeAOT 后还能把 launcher AOT 成原生、彻底无 vm（见 Deferred）。
- **runtime package** = z42vm + libs + z42c，**不含 launcher**。

**第一次执行（`install-z42`，唯一保留的原生 bootstrap；shell/PS + 系统 curl）一次装完即功能完整**：

```
1. 探测 host RID（uname/arch）
2. curl 拉 channel manifest：releases/latest/download/release-index.json（或 nightly tag）
3. 从 manifest 取两个 archive + sha：launcher bundle、host runtime
4. 下载两者 → 校验 → 解压：
     launcher → ~/.z42/bin/z42 + ~/.z42/launcher/launcher.zpkg
     runtime  → ~/.z42/runtimes/<ver>/；config.toml default=<ver>
5. ~/.z42/bin 入 PATH（或打印指引）
6. 完成 —— z42 list / run / build 立即可用（launcher 用刚装 runtime 的 vm 跑）
   （项目本地变体：装进 <repo>/.z42，隔离/pin，沿用现状）
```

**之后全部 z42 驱动**（跑在已装 runtime 的 vm 上）：`z42 install / update / self update / workload install / …`。

**为何这样**：bootstrap 下载交 shell/系统 curl（install-z42 本就是 shell）→ launcher 无需带 TLS 的 vm；**对齐 dotnet**（shell 装 SDK → 之后托管命令管理）。launcher 逻辑仍是 z42（满足 z42 优先）。
**边角**：`z42 uninstall` 删光最后一个 runtime → launcher 无 vm 可跑 → 挡一下（拒删最后一个 / 提示重跑 install-z42）。dotnet 同性质。

## `$Z42_HOME` 布局

```
~/.z42/
  bin/z42                  launcher 原生 trampoline（薄；self update 目标）       ┐ launcher bundle
  launcher/launcher.zpkg   launcher 逻辑（z42；跑在已装 runtime 的 vm 上，无自带 vm）┘（与 runtime 独立更新）
  config.toml              default = "<ver>"  ·  channel = stable|nightly
  cache/                   下过的归档（校验通过再解压；断点/复用）
  runtimes/
    <ver>/                 ① host runtime（z42vm + libs + z42c；不含 launcher）
      workloads/<wl>/      ③ 版本作用域目标产物（target runtime pack + 平台命令；ABI 配套 <ver>）
  tools/                   全局用户工具（类 ~/.cargo/bin，版本无关）
```

三类可安装物：**① runtime 版本（side-by-side）· ② launcher 自身（trampoline + launcher.zpkg，无 vm）· ③ workload（挂某 runtime 版本下）**。

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
- `z42 self update` → 换 **launcher bundle**（`bin/z42` trampoline + `launcher/launcher.zpkg`）：下 launcher archive → 原子替换（trampoline 替换是唯一须碰 native 自替换处）。launcher 不带 vm（复用 runtime vm），故 self update 不涉及 vm。
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
| 6 | launcher 与 runtime 拆分；**launcher 不带 vm，复用已装 runtime 的 vm** | 不必为 launcher 造/最小化 vm；NativeAOT 后转原生 |
| 7 | 首次 `install-z42` 一次装 launcher + host runtime（manifest 驱动），之后全 z42 驱动 | "第一次执行完成安装"；下载交 shell（无需带 TLS 的 launcher vm），对齐 dotnet |
| 8 | `runtime` 为 per-RID 名词/组件，但 `install` 只装 host；target runtime 仅经 workload 组合进 | 命名诚实 + 用户面无冗余直装路径 |

## Deferred / 待 spec 细化

- **launcher 最小 vm → 由 NativeAOT 原生 launcher 取代**：本设计 launcher 已不带 vm（复用 runtime vm），故现在无需最小化 vm；NativeAOT 落地后把 launcher AOT 成原生二进制（rustup 式），vm 问题永久消失。届时 `install-z42` 可只下原生 launcher，bootstrap 更轻。
- manifest schema 定稿（多 archive 类型、可选 z42c-less 精简 runtime、依赖/兼容区间）。
- self update 的原子替换 + 回滚（Windows 文件占用、权限）。
- workload↔runtime 版本兼容矩阵与升级时的自动重装策略。
- 离线/镜像源（企业内网 mirror base URL）。
- 签名/校验链（manifest 签名 + archive 校验）。
- `z42 use` 范围 pin（`0.3.x`）的解析与自动安装策略。
