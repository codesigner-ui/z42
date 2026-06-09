# Tasks: apphost-out-path

> 状态：🟢 已完成并归档（2026-06-10）| 续作 add-apphost + simplify-apphost-direct-run

**变更说明（User 裁决）**：给 apphost patcher 加两条输入路径，让 `./xtask`（仓库根原生 apphost，直跑 `artifacts/xtask/xtask.zpkg`）可由 **toml 配置 + 一条命令**产出，**无 wrapper 脚本**：

1. `z42 apphost build <app.zpkg> --out <path>`：exe 写到任意 `<path>`，内嵌 app.zpkg **相对 exe 所在目录**的路径（运行时按 `current_exe` 解析 ⇒ 可整体搬迁）。
2. `z42 apphost build <project.z42.toml>`（**主用法**）：读 `[apphost].publish_dir` + 从 `[build]`/`[project]` 推已编译 zpkg → 产 exe 到 `publish_dir/<name>`。`scripts/xtask.z42.toml` 配 `[apphost] publish_dir=".."` → `z42 apphost build scripts/xtask.z42.toml` 在仓库根产 `./xtask`。

默认模式（exe 同 zpkg 目录 + basename，改名安全）不变。

**原因（User）**：「让 xtask 也使用 apphost 编译，然后输出到根目录」+「尽量减少命令行脚本，xtask.z42.toml 配置好输出路径」+「配置 apphost，直接使用这个模式编译」。先做的 `--out` + `scripts/build-xtask.sh` 被 User 否决（不要 wrapper 脚本）→ 改为 **toml 即唯一真相**：配置进 `xtask.z42.toml`，patcher 读 toml。

**设计选择（User 选 B）**：「patcher 读 toml」而非「z42c build 自动产 apphost」。消费逻辑全留 z42（launcher.zpkg patcher），符合"z42 优先"；C# `ProjectManifest` 仅登记 `[apphost]`/`publish_dir` schema 以免 WS008，**不耦合** C# 驱动到 patcher。supersedes Deferred `apphost-future-build-flag`（已从 roadmap Deferred Index 移除）。

**子系统**：`toolchain`（patcher `core/apphost.z42` + launcher manifest 加 z42.toml dep）+ `compiler`（`ProjectManifest.cs` 登记 schema + 2 单测；与 add-reflection-type-flags 文件不重叠）。User 授权并行例外（disjoint files）。feat 型。

- [x] 1.1 `core/apphost.z42`：`Build` 三模式分支（默认 / `--out` / `.toml`）；toml 分支用 `Std.Toml` 读 `[apphost].publish_dir`+`[project].name`+`[build]`（`${output_dir}` 单层展开），推 zpkg + 校验存在 + 推 outPath。抽 `_emit`（patch+write+chmod+macOS 重签名）共享三模式。
- [x] 1.2 `core/apphost.z42`：`_relPath` 用的 `_splitSlash` 加规范化（丢 `.`、折叠 `..`）→ toml 推出的 `scripts/../artifacts/...` 正确算成 `artifacts/xtask/xtask.zpkg`。新增 `_tomlStr`/`_joinProj`。
- [x] 1.3 `src/toolchain/launcher/core/z42.launcher.z42.toml`：加 `z42.toml = 0.1.0` dep（patcher 读 manifest）。
- [x] 1.4 `scripts/xtask.z42.toml`：加 `[apphost] publish_dir=".."`（project-dir 相对 → 仓库根）。
- [x] 1.5 `src/compiler/z42.Project/ProjectManifest.cs`：`apphost` 进 `KnownTopLevelKeys` + `KnownApphostKeys={publish_dir}` + 段扫描（消 WS008；陌生子 key 仍 WS008）。**不**进 record（z42c 不消费）。
- [x] 1.6 单测 `ProjectManifestTests`：`Apphost_KnownSection_NoWarning` + `Apphost_StrayKey_TriggersWS008`（43/43 绿）。
- [x] 1.7 `.gitignore`：`/xtask`（原生 + 平台相关 + 重生不提交）。
- [x] 1.8 docs：`launcher.md`（三模式 + `[apphost]` 配置段 + 历史决策）、`building/README.md`（两条命令产 `./xtask`）、roadmap Deferred Index 删旧条目。
- [x] 1.9 e2e：`z42 apphost build scripts/xtask.z42.toml` → `./xtask`（embed=`artifacts/xtask/xtask.zpkg`，macOS 重签名通过）→ 跑出 xtask usage（仓库根 `.z42/` 本地 runtime 解析 z42vm+libs），exit 0。

## 备注
- `scripts/build-xtask.sh`（首版 `--out` wrapper）**已删**——User 要求减脚本，toml-mode 取代之。
- patcher 的 `${output_dir}` 只做单层字面展开（覆盖 xtask + 常见情形）；嵌套 / 多 token 模板未支持（当前无此用例）。
- stub/lib.rs/Cargo bins/macOS 重签名/dist smoke 均**不变**。
