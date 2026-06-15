# building/

按"我要 build 什么"挑文件。每份文档都是**编号 step 配方**，从零开始可直接照抄。

| 文件 | 用途 |
|------|------|
| [`compiler.md`](compiler.md) | C# 编译器 / `z42c` 命令 |
| [`vm.md`](vm.md) | Rust VM / `z42vm` + feature flag |
| [`stdlib.md`](stdlib.md) | 22 个 stdlib 包 workspace |
| [`cross-platform.md`](cross-platform.md) | 桌面跨平台 build matrix（placeholder 0.2.5）|
| [`wasm.md`](wasm.md) | 🟢 WASM facade（`@z42/wasm` npm 包）|
| [`ios.md`](ios.md) | 🟢 iOS facade（`Z42VM.xcframework` SwiftPM 包）|
| [`android.md`](android.md) | 🟢 Android facade（`z42vm.aar` AAR module）|

桌面日常用 **xtask**（编译产物 `artifacts/xtask/xtask.zpkg`，源码 `scripts/xtask*.z42`）：`./xtask build [all]` / `./xtask test`。

更省事：把 xtask 编成一个**原生 apphost** `./xtask`（仓库根），直接 `./xtask build [all]` / `./xtask test`，免走 `z42 …zpkg` 入口。产出（两条命令，无 wrapper 脚本）：

```bash
dotnet run --project src/compiler/z42.Driver -- build scripts/xtask.z42.toml --release   # 编 xtask.zpkg
z42 apphost build scripts/xtask.z42.toml                                                  # 读 [apphost] → ./xtask
```

`./xtask` 原生 + 平台相关 + 已 gitignore（重生不提交）；机制与 `[apphost].publish_dir` 配置见 [`runtime/launcher.md`](../../design/runtime/launcher.md#z42toml-配置apphost-publishapphost-out-path-2026-06-10)。

平台 facade 的源码 + 跨平台契约见 [`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)；设计与决策见 [`docs/spec/`](../../spec/)。
