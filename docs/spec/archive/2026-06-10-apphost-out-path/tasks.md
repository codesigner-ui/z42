# Tasks: apphost-out-path

> 状态：🟢 已完成并归档（2026-06-10）| 续作 add-apphost + simplify-apphost-direct-run

**变更说明（User 裁决）**：给 `z42 apphost build` 加 `--out <path>`：exe 可写到任意位置、内嵌 app.zpkg **相对 exe 所在目录**的路径（相对 = 可整体搬迁）。配 `scripts/build-xtask.sh` 在**仓库根**产出 `./xtask`（apphost for `artifacts/xtask/xtask.zpkg`），于是 `./xtask build package --rid macos-arm64` 直跑，免敲 `z42 artifacts/xtask/xtask.zpkg -- build package …`。

**原因（User）**：「让 xtask 也使用 apphost 编译，然后输出到根目录，这样就可以直接使用 xtask 启动了，不用通过 z42 为入口，大大简化了」。原 apphost 只支持「exe 放 app.zpkg 同目录、内嵌 basename」一种模式，无法把 exe 放到与 zpkg 不同的目录（如仓库根 vs `artifacts/xtask/`）。

**子系统**：`toolchain`（launcher patcher `core/apphost.z42` + `scripts/build-xtask.sh`；与 port-z42c-core 并行，User 授权，文件不重叠）。feat 型。

- [x] 1.1 `src/toolchain/launcher/core/apphost.z42`：`Build` 解析 `--out <path>`；两模式分支——默认（exe 同目录 + basename，旧行为不变）/ `--out`（exe 任意位置 + 相对路径 embed）。
- [x] 1.2 新增 `_relPath(fromDir, toPath)`（两端解析 CWD → 绝对 → 拆段 → 公共前缀 → `../`+剩余）+ `_splitSlash(s)`（手写按 `/` 拆非空段，z42 无 `String.Split`）。
- [x] 1.3 `scripts/build-xtask.sh`：编 apphost stub（cargo）+ launcher.zpkg + xtask.zpkg（dotnet driver）→ 经 `z42vm launcher.zpkg -- apphost build artifacts/xtask/xtask.zpkg --out xtask`（设 `Z42_APPHOST_TEMPLATE` + `Z42_LIBS`）patch → `./xtask`。`--no-build` 跳过三步只重 patch；缺 z42vm/stdlib 给指引性报错。
- [x] 1.4 `.gitignore`：加 `/xtask`（原生 + 平台相关 + 重生不提交）。
- [x] 1.5 `docs/design/runtime/launcher.md` apphost 段同步两输出模式 + `current_exe` 相对解析 + `./xtask` 用法 + build-xtask.sh。
- [x] 1.6 e2e：`build-xtask.sh --no-build` → `./xtask` 跑出 xtask usage（仓库根 `.z42/` 本地 runtime 解析 z42vm+libs），macOS 重签名通过，exit 0。

## 备注
- patcher 的 `--out` embed 假定输入路径相对干净（无内嵌 `..`）；`build-xtask.sh` 传 `artifacts/xtask/xtask.zpkg` + `--out xtask` 满足。
- stub/lib.rs/Cargo bins/macOS 重签名/dist smoke 均**不变**——本变更纯加 patcher 的输出模式 + 一个便捷脚本。
