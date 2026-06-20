# Tasks: 发行包不再打包 examples

> 状态：🟢 已完成 | 创建：2026-06-20 完成：2026-06-21 | 类型：refactor（toolchain packaging）

**变更说明：** 所有发行包（desktop SDK / 各平台 workload tooling）不再产出 `examples/` 目录（hello_c / hello_rust）。SDK 瘦身 + 示例职责后移到 workload（已 in-flight 的 `add-workload-command-dispatch` 走 `examples/workloads/greet/`）。

**原因：** User 裁决（2026-06-20，范围 A）：只从打包产物删，`examples/` 源目录保留——它是 C# 测试 + zbc_compat 载重夹具（roadmap `infra-extract-user-docs` 明确"留仓不外迁"）。

**文档影响：** embedding.md / launcher.md / roadmap.md 中"examples 随包分发 / 包形态含 examples/"的描述需同步为"不再随包分发"。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/xtask_package.z42` | MODIFY | 删 `Directory.Create(pkgDir/examples)`（L42）+ examples/hello_c SHA256 校验段（L211-214）|
| `scripts/xtask_package_desktop.z42` | MODIFY | 删 [5/7][6/7] 调用 + `_pkgEmitHelloC`/`_pkgEmitHelloRust`/`_quotedSubdirs` 函数 + manifest examples 字段；步骤重编号 /5 |
| `scripts/xtask_package_android.z42` | MODIFY | 删 `_pkgEmitHelloC` 调用（L117-118）+ manifest examples 字段（L212,223）|
| `scripts/xtask_package_ios.z42` | MODIFY | 删内联 hello_c 复制块（L132-141）+ manifest examples 字段（L145,156）+ 注释 examples 提及 |
| `scripts/xtask_package_wasm.z42` | MODIFY | 删内联 hello_c 复制块（L95-104）+ manifest examples 字段（L166,181）|
| `docs/design/runtime/embedding.md` | MODIFY | "examples 随 SDK 分发 / 包形态含 examples/ / hello_c byte-identical invariant" 同步 |
| `docs/design/runtime/launcher.md` | MODIFY | 包布局 L110/L151 含 examples/ → 移除 |
| `docs/roadmap.md` | MODIFY | L218 包形态 `bin/libs/native/examples/manifest.toml` → 去 examples |

**只读引用：**
- `scripts/xtask_package.z42` `_quotedDirList`/`_quotedExisting`（manifest 其他字段，保留）

## 任务
- [x] 1.1 xtask_package.z42：删 examples 目录创建 + SHA256 校验段
- [x] 1.2 xtask_package_desktop.z42：删 examples 步骤/函数 + manifest 字段 + 重编号 /5
- [x] 1.3 xtask_package_android.z42：删 hello_c 调用 + manifest 字段（含空 [contents] 表头）
- [x] 1.4 xtask_package_ios.z42：删内联 hello_c 块 + manifest 字段 + 注释
- [x] 1.5 xtask_package_wasm.z42：删内联 hello_c 块 + manifest 字段
- [x] 1.6 docs 同步（embedding.md / launcher.md / roadmap.md）
- [x] 2.1 xtask 全量重编（rm .cache 强制）**无 error**；5 个 package .zbc strings 验证 **examples 0 次**、xtask.zpkg **hello_c 0 次** → 改动确证落进 zpkg
- [x] 2.2 完整 `package release --rid macos-arm64` 端到端：SDK 包**无 examples/**（bin/native/libs/manifest 完好）；`test dist` 377/0（2026-06-21，随 rework-host-runtime-pack 一并验证）

## 备注
- manifest `examples` 字段无任何 install/launcher 消费者（已 grep 确认），整行删除安全。
- `examples/` 源目录 + 其 C# 测试/zbc_compat 消费不动。
