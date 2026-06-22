# Proposal: bootstrap-z42c-from-nightly (replace-csharp S4)

## Why

replace-csharp-compiler S4：建立**脱离 C# 重建 z42c 的闭环**——这是删 C#（S5）的唯一前置（铁律）。
当前每条 bootstrap 都依赖 C#：源 bootstrap 用 `dotnet build slnx` + `dotnet -- build`（primer stdlib + xtask + z42c）；下载 bootstrap 也用 `dotnet -- build` 编 xtask。S3 的 `_buildStdlibCore` flip 内部种子也是 C# DLL（`_csharpBuildStdlibSeed`）。

自举不动点已验证（exploration 2026-06-22）：z42c-built `z42c.driver.zpkg` 与 C#-built **逐字节一致**，单 toml 逐成员忠实编译 z42c 自身（无 C#）。缺的只是**一个预建的 z42c-written 种子来源**让 fresh checkout / CI / dev 不经 C# 即可重建 z42c。

User 决策（2026-06-22）：种子**从 nightly 下载**（不 commit 进仓库；与 repo 瘦身一致；复用现有 install-z42 下载基建）。

## 关键约束（exploration 发现）

- **nightly 现 ship 的是 C# z42c**（`bin/z42c` = `dotnet publish` 单文件，xtask_package_desktop.z42 [1/5]），**不是 z42c-written 编译器**。真正的 S4 种子必须是 **z42c-written** 编译器（`z42c.*` 7 个 zpkg），否则"用下载的 C# z42c 编 z42c"仍是 C# 实现在编译，非自举。
- 种子要能**运行**：z42c.driver.zpkg 依赖其余 6 个 z42c.* + stdlib（运行期 Z42_LIBS）。故 nightly 须含 {z42c.* 7 + 一份可跑的 stdlib}。
- **z42vm 不是 C# 问题**：Rust，`cargo build` 本地产，永远脱 C#。S4 只需解决 z42-language 产物（z42c + stdlib）的"先有鸡"。
- **`z42c build --workspace` 自建 z42c 有 E0402 wrinkle**（fresh-sibling/stdlib 解析）；**单 toml 逐成员拓扑**自建已验证 OK → C#-free 重建走单 toml 路径（镜像 `_testZ42cSelfHostByteIdentical`）。
- **鸡蛋滚动**：种子由 source-bootstrapped publish-nightly（用 C#）产 —— S4 阶段允许（C# 造种子）；S4 目标是**下游/CI/dev 重建 z42c 不需 C#**。S5 删 C# 后 publish-nightly 自身改用前一夜种子（自持闭环，S5 收尾）。

## What Changes

1. **nightly 携带 z42c-written 种子**：publish-nightly 的产物（runtime artifact 或新 seed artifact）加入 z42c-written `z42c.*` 7 zpkg（+ 复用其 stdlib）。
2. **install-z42 / 下载侧取种子**：把 z42c.* 一并下载进 `.z42/`（与 z42vm + stdlib 同处）。
3. **C#-free bootstrap 路径**：新 `xtask-bootstrap-no-csharp`（或脚本）——`cargo build z42vm` + 下载种子 → 种子 z42c 编 stdlib（源）→ 种子 z42c 编 z42c（源，单 toml 拓扑）→ fresh z42c 编 xtask。**全程无 dotnet**。
4. **flip 接受 z42c 种子**：`_buildStdlibCore` 的种子步可用下载的 z42c 种子替代 C# DLL（C#-free 路径下）。
5. **CI job `bootstrap-no-csharp`**：从下载 nightly 跑 C#-free bootstrap，断言无 dotnet 调用 + 成功 + 不动点（rebuilt z42c == 种子，逐字节 ignore BLID）。

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `scripts/xtask_package_desktop.z42` | MODIFY | nightly/release 产物加入 z42c-written z42c.* 7 zpkg（种子 layout） |
| `scripts/install-z42.sh` | MODIFY | 下载侧把 z42c.* 取进 `.z42/`（seed dir） |
| `.github/actions/xtask-bootstrap-no-csharp/action.yml` | NEW | C#-free bootstrap composite（cargo z42vm + 下载种子 + 种子 z42c 重建 stdlib/z42c/xtask） |
| `scripts/bootstrap-no-csharp.sh` | NEW | 可本地跑的 C#-free 重建脚本（action 调它；本地用当前产物当种子验证） |
| `scripts/xtask_stdlib.z42` | MODIFY | `_buildStdlibCore` 种子步支持 z42c 种子（env 选择 C# DLL vs 下载 z42c 种子） |
| `.github/workflows/ci.yml` | MODIFY | 新 job `bootstrap-no-csharp (linux-x64)` |
| `docs/design/compiler/self-hosting.md` | MODIFY | S4 闭环 + 种子机制文档 |
| `docs/spec/changes/replace-csharp-compiler/tasks.md` | MODIFY | S4 勾选 |

**只读引用**：`.github/actions/xtask-bootstrap-source/action.yml`、`xtask_package.z42`、`scripts/xtask_compiler_z42.z42`（`_testZ42cSelfHostByteIdentical` 单 toml 模式参考）。

## Out of Scope

- S1 native z42c apphost（独立；本 change 种子用 z42vm + z42c.driver.zpkg，不强制 apphost）。
- S5 删 C# / publish-nightly 自身改 C#-free（S5）。
- `z42c build --workspace` 自建 E0402 根因修复（单独 change；本 change 用单 toml 绕过）。

## Open Questions

- [ ] 种子在 nightly 的精确 layout（runtime artifact 内 `z42c/` 子目录 vs 独立 `z42c-seed` artifact）——倾向 runtime artifact 内 `z42c/`（install-z42 已下 runtime）。
- [ ] CI job 触发面（push/PR paths）+ 是否进 publish-nightly needs（不进，避免鸡蛋）。

## 验证现实

- **本地可验证**：`bootstrap-no-csharp.sh` 用**当前 build 产物当种子**跑全程无 dotnet 重建 + 不动点（不依赖真 nightly）。
- **仅 CI 可验证**：真·下载 nightly → C#-free bootstrap（需先 publish 一个含 z42c 的 nightly；滚动自愈）。
