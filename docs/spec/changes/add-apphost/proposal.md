# Proposal: apphost —— 每-app 原生可执行文件（framework-dependent）

## Why

今天 launcher 是两层（原生 trampoline `z42` + `launcher.zpkg`），用法是把 app 当**参数**传：`z42 app.zpkg`。这缺了 .NET apphost 的核心体验——**一个以 app 命名、直接就是 app 的原生可执行文件 `./myapp`**：双击 / 直接跑，而不是 `z42 myapp.zpkg`。

`z42c build` 现在只产 `app.zpkg`；要跑必须先有 `z42` 在 PATH 上。引入 apphost 后，`build` 额外产出一个原生 stub `./app`，它自己定位 VM + stdlib 运行时并起 app——分发体验对齐 `dotnet publish` 的 apphost。

不做的代价：z42 app 永远要"先装 z42 launcher 再 `z42 app.zpkg`"，无法像普通可执行文件那样直接交付。

## What Changes

- **新增原生 apphost stub**（与 trampoline 同 crate 的第二个 bin）：内嵌**占位符字节区**（.NET 同款），build 时被覆写成本 app 的 zpkg 相对路径；运行时读占位符 → 定位 `<exe同级>/<app>.zpkg`，**改名安全**。
- **新增 `z42 apphost build <app.zpkg>` 命令**（z42 写，落在 `launcher.zpkg`）：拷贝 apphost 模板 + patch 占位符 + 置可执行位，产出 `./<app>`。
- **本地优先的 framework-dependent 运行时解析**（仅本变更范围）：apphost stub 按 `$Z42_HOME` 覆写 → **本地 `.z42`（exe 目录上行）** → 系统 `$HOME/.z42` 顺序定位 launcher 运行时；找到后 `exec z42vm launcher.zpkg -- <app.zpkg> -- <argv>`，**复用 launcher 核心已有的裸 apphost 形式**做版本/`runtimeconfig.json` 解析（符合"z42 优先"铁律）。
- **共享 host-resolve lib**：把 trampoline 与 apphost 共用的 `z42_home` / 运行时探测 / exec 抽到 `lib.rs`，trampoline 改用之（消除重复）。

> **本地目录查询规则**（User 在叉路上明确要求"要确定"）：见 design.md **Decision 3**，gate 处请 User 拍板。

## Scope（允许改动的文件）

> ⚠️ **实现排队**：`toolchain` 子系统当前被 `port-z42c-core` 持有（见 `ACTIVE.md`）。本变更**只在 docs 阶段推进**（proposal/design/spec/tasks，docs 不上锁）；**阶段 7 实施须等 `port-z42c-core` 归档释放 toolchain 锁**后占锁再开工。下表是实施时唯一允许触及的清单。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/launcher/src/lib.rs` | NEW | 共享 host-resolve：`Runtime` 结构、`z42_home`、运行时探测、`exec_core`；trampoline 与 apphost 复用 |
| `src/toolchain/launcher/src/apphost.rs` | NEW | apphost 原生 stub：读内嵌占位符 → 本地优先解析 → exec；含占位符 magic + `#[used]` static |
| `src/toolchain/launcher/src/main.rs` | MODIFY | trampoline 改调 `lib.rs`，去掉内联 `resolve_runtime` 重复 |
| `src/toolchain/launcher/Cargo.toml` | MODIFY | 加 `[lib]` + apphost `[[bin]]`（`src/apphost.rs`） |
| `src/toolchain/launcher/core/apphost.z42` | NEW | `z42 apphost build` 命令：定位模板 + `PatchBytes`（纯函数）+ 写出 + chmod + **macOS ad-hoc 重签名（Decision 7）** |
| `src/toolchain/launcher/core/launcher.z42` | MODIFY | 命令分发加 `apphost` 分支 + help（逻辑落新文件 apphost.z42） |
| `src/toolchain/launcher/core/z42.launcher.z42.toml` | MODIFY | 加 `z42.encoding` 依赖（`*.z42` glob 已自动纳入 apphost.z42） |
| `src/toolchain/launcher/README.md` | MODIFY | 文档新 lib / apphost bin / `apphost build` 命令 |
| `scripts/xtask_package.z42` | MODIFY | 把 `apphost` bin 铺进 `<pkg>/bin/`（Decision 8，folded-in）|
| `scripts/install.sh` | MODIFY | 把 `apphost` 装到 `$Z42_HOME/launcher/apphost`（Decision 8）|
| `scripts/xtask_test_dist.z42` | MODIFY | apphost smoke：build app → `apphost build` → 跑产出 exe → 断言（取代原 core/tests [Test]，因 launcher core 无 test-runner 接线）|
| `docs/design/runtime/launcher.md` | MODIFY | 新增 "apphost" 段：机制 + 本地优先解析规则 + 签名 + Deferred |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记 `add-apphost`（docs，不上锁） |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 加 self-contained / single-file / build-flag / windows-checksum / cross-sign 索引行 |

**只读引用**（理解上下文必须读，不修改）：

- `docs/design/runtime/embedding.md` — 单文件自包含（P4）所需 C ABI（`z42_host_load_zbc` 内存加载）参考
- `docs/design/runtime/zpkg.md` — Exe-mode `META.entry` 语义
- `docs/design/runtime/vm-architecture.md` — VM 启动 + `Z42_LIBS` 解析顺序
- `src/runtime/src/main.rs` — VM 的 libs 搜索回退链（与 apphost 设的 `Z42_LIBS` 衔接）

## Out of Scope（延后，各自独立 change）

- **self-contained 模式**：VM + libs 随 app 本地化（`--self-contained`）。P1 只做 framework-dependent（不内置运行时）。→ design Deferred `apphost-future-self-contained`
- **single-file 自包含**：apphost 链接 `libz42_vm` + 内嵌 zpkg/libs，走 embedding C ABI 内存加载。依赖 C ABI 成熟，且碰 `runtime` 锁。→ design Deferred `apphost-future-single-file`
- **`z42c build --apphost` 便捷 flag**：build 完自动调 patcher。碰 `compiler` / `z42c` 锁，单独 change。→ design Deferred `apphost-future-build-flag`
- ~~发布包内置 apphost 模板~~：**已折进 P1**（User 裁决 2026-06-09，Decision 8）——`build package` 铺 `bin/apphost`、`install.sh` 装、dist smoke 覆盖。
- **cwd 上行搜索 / 富搜索路径配置**：P1 本地搜索只从 exe 目录上行；cwd 上行与可配置搜索路径延后。
- **Windows PE 校验和 / Authenticode、跨平台交叉签名**：P1 macOS 用 host `codesign`；Windows 无签名 exe 直接跑。→ design Deferred `apphost-future-windows-checksum` / `apphost-future-cross-sign`

## Open Questions

- [ ] **Decision 3（本地查询规则）**：exe 目录上行找 `.z42`——是否同时也从 cwd 上行？P1 推荐"仅 exe 目录上行 + `$Z42_HOME` 覆写 + `$HOME/.z42` 兜底"，待 User 在 gate 确认。
- [ ] **模板定位**：`z42 apphost build` 从哪取 apphost 模板？推荐"与 `z42vm` 同级的 runtime bin 目录"，dev 期落 cargo 产物路径。待 design 定稿。
- [ ] **patcher 归属**：放 `launcher.zpkg` 内（复用其 file/path/process helper + 命令分发，已随包 ship）vs 新建 packager zpkg。推荐前者，见 design Decision 4。
