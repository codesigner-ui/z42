# Tasks: rework-desktop-publish-run — 🟢 已完成

**变更说明：** 定稿核心命令模型（字节码模型下 build/run/publish/export/test 边界 + run 双形态）；把 `export desktop` rework 成 **`publish desktop`**（release 部署件）+ 新增 **`run desktop`**（debug apphost 旁 zpkg 临时跑）。
**原因：** apphost 是 publish 产物（desktop 无 IDE 工程可"export"）；`run <plat>` 统一为"以该平台形态跑"。User 确认 2026-06-17。
**锁：** `toolchain`。

## 设计落地
- [x] 1.1 platform-export-lifecycle.md：扩动词段——字节码模型 + build/run(双形态)/publish/export/test 边界 + run zpkg-vs-apphost
- [x] 1.2 launcher-command-dispatch.md：命令模型 + apphost 归属（publish/run）+ export 仅 ios/android

## 代码 rework
- [x] 1.3 launcher_export.z42：`_cmdExportDesktop`→`_cmdPublishDesktop`（release，publish_dir）；新增 `_cmdRunDesktop`（debug apphost **旁 zpkg** + exec，转发 `-- args`）；`_desktopResolveZpkg` 共享
- [x] 1.4 launcher_cli.z42：export router 仅 ios/android/wasm；加 `publish` router(desktop)；`run desktop` 走 run 透传
- [x] 1.5 xtask_test_dist.z42 smoke：`export desktop`→`publish desktop`
- [x] 1.6 docs：launcher.md / export.md（desktop 出 export 表→publish）/ project.md / launcher README / src/toolchain README / workflow / 各代码注释

## GREEN
- [x] 1.7 launcher 清编 exit 0；**运行级**：`z42 publish desktop scripts/xtask.z42.toml`→./xtask 产出且跑通；`z42 run desktop scripts/xtask.z42.toml -- --help` 旁-zpkg apphost + exec 跑通（exit 0）
- [x] 1.8 COMMIT + 归档

## 关键设计点
- **run desktop 的 apphost 产在 zpkg 同目录**（非 temp）：apphost 靠 walk-up 找 `.z42`(项目/全局)解析 vm，/tmp 无此祖先会失败；旁 zpkg = 正常部署解析语境。
- mobile run-on-device / publish .ipa/.aab、wasm bundle = 未来 B5（本 change 只 desktop + 落模型）。
