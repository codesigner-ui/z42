# Proposal: z42 launcher (`z42`) — 原生 trampoline + z42 核心

## Why

今天跑 z42 程序要么 `z42vm app.zbc`(裸,需手传 entry + 无 argv),要么 `z42c run`(编译器兼当 runner,且**不转发脚本参数**——见 BuildCommand.cs)。两条都是错位:
- `z42c` 是**编译器**,以后要用 z42 重写自举,不该承载"运行 + 传参 + 选版本"的运行期职责。
- 没有统一的"给我一个 app,自动选对版本的运行时跑起来"入口;分发(下载即用)也无处落脚。

引入 **`z42` launcher**:用户**一次性安装的唯一入口**,负责"解析所需运行时版本 → 用对应 z42vm 跑 Exe-zpkg → 透传命令行参数",并管理已装运行时。类比 `dotnet` muxer + `rustup`。

**z42 优先(本变更硬约束)**:凡能用 z42 写的都用 z42。bootstrap 铁律(无 VM 跑不了 z42)决定只有"找/给 VM 的最小核"必须原生;其余逻辑全部 z42。

## What Changes

- 新增**原生 trampoline `z42`**(Rust,极小):用随装的"launcher 运行时"(pinned z42vm + `launcher.zpkg`)`exec z42vm launcher.zpkg -- <argv>`,回传退出码。仅此。
- 新增 **z42 写的 launcher 核心** `launcher.z42`:argv 解析 / 子命令 / `~/.z42` 缓存读写 / 版本解析 / 用 `Std.IO.Process` 起目标 app 的 z42vm。
- **前置使能(Phase 0)**:
  - z42vm CLI 接受收尾 `-- <args>`,填进运行时 `Environment.GetCommandLineArgs()`(durable,在 Rust 运行时)。
  - z42c `build` 对单文件 script emit **Exe-mode zpkg**(autodetect Main → `META.entry`)。
- **P1 命令**:`run` / `link` / `list` / `default` / `which` / `info`(本地、无网络)。
- **不在本 spec**(留后续):`install` / `uninstall` / `self update` / 下载+校验(P2);app 自带版本声明格式(META 字段 vs runtimeconfig)——P1 用 `z42 link` + `z42 default` 本地指定。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/launcher/Cargo.toml` | NEW | 原生 trampoline crate |
| `src/toolchain/launcher/src/main.rs` | NEW | trampoline:定位 launcher 运行时 + exec + 回传码 |
| `src/toolchain/launcher/README.md` | NEW | 目录职责(code-organization 要求) |
| `src/toolchain/launcher/core/launcher.z42` | NEW | z42 launcher 核心(argv/子命令/缓存/resolve/exec) |
| `src/toolchain/launcher/core/<...>.z42` | NEW | 核心拆分文件(cli/resolve/cache/exec/info,按 LOC 限) |
| `src/toolchain/launcher/core/z42.<lib>.toml` | NEW | launcher 核心的 zpkg 工程文件 |
| `src/toolchain/launcher/tests/*.z42` | NEW | 核心 [Test] 用例 |
| `src/runtime/src/main.rs` | MODIFY | `Cli` 加收尾 `args: Vec<String>`(trailing_var_arg)→ 透传 argv |
| `src/runtime/src/vm_context.rs` | MODIFY | 存储 program argv,供 GetCommandLineArgs 读取 |
| `src/runtime/src/corelib/<env module>.rs` | MODIFY | `GetCommandLineArgs` 返回透传的 argv（定位后填具体文件） |
| `src/compiler/z42.Driver/BuildCommand.cs` | MODIFY | `build` script 模式 emit Exe-zpkg(META.entry) |
| `docs/design/runtime/launcher.md` | NEW | launcher 架构 + ~/.z42 布局 + 命令 + resolve 长期规范 |
| `docs/design/runtime/vm-architecture.md` | MODIFY | argv 透传机制 |
| `docs/roadmap.md` | MODIFY | 移除/更新 build-driver 相关条目;登记 launcher |

**只读引用**：`docs/design/runtime/zpkg.md`(META/Exe 模式)、`scripts/*.z42` + 瘦 shim(cutover 参考)、`src/runtime/src/corelib/process.rs`(Process API)。

## Out of Scope

- 下载/安装/自更新/网络(P2,独立 spec)。
- app 自带版本声明格式(META `toolchain_version` / runtimeconfig.json)——P1 不依赖。
- 分发打包(packager 的活)。
- `z42 build` 转发编译器等 SDK-muxer 功能。

## Open Questions

- [ ] trampoline 二进制名定 `z42`(umbrella,类 dotnet)还是 `z42up`(管理器,类 rustup)?本 spec 取 **`z42`**。
- [ ] launcher 核心 zpkg 与 launcher 运行时的版本耦合:随装捆绑一个 pinned 运行时(本 spec 取此)。
