# Proposal: 定义跨平台 SDK package 目录约定（define-package-layout）

## Why

4 个嵌入平台 facade（host / iOS / Android / wasm）的 `./build.sh` 都能产工作的 facade artifacts，**但散在 `platforms/<plat>/` 下各自一套形态**：

| 平台 | 产物位置（当前） | 用户可消费形态 |
|---|---|---|
| host | `artifacts/packages/z42-<v>-<rid>-<config>/` | ✅ 完整 SDK |
| iOS | `platforms/ios/Z42VM.xcframework/` + `Resources/stdlib/` | ❌ 散在仓库内，未规整 |
| Android | `platforms/android/z42vm/build/outputs/aar/z42vm-release.aar` | ❌ build/ 内，未导出 |
| wasm | `platforms/wasm/{pkg-web,pkg-nodejs}/` + `js/stdlib/` | ⚠️ 散，未 publish-ready |

**核心问题**：
1. 用户拿不到 "一个目录 = 一个平台 SDK" 的产物
2. C 嵌入者跨平台体验不一致（每平台目录结构都不同）
3. 没有 manifest 描述包含什么 / 版本 / 兼容 ABI

**本 spec 定义**：13 个 per-arch package 共用的目录约定 + `manifest.toml` schema + 跨 package 一致性 invariant，作为下游 Phase 1.1–1.4 spec 的**契约**。

## What Changes

本 spec **只产规范文档**（4 份 spec doc + 2 处 design doc 同步），不产任何代码。下游 4 个 spec 实施时按本契约产实际 packages。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `docs/spec/changes/define-package-layout/proposal.md`                          | NEW | 本文件 |
| `docs/spec/changes/define-package-layout/design.md`                            | NEW | D1–D9 决策落字 |
| `docs/spec/changes/define-package-layout/specs/package-layout/spec.md`         | NEW | Requirement 1–9（per-arch flat + manifest）|
| `docs/spec/changes/define-package-layout/tasks.md`                             | NEW | 实施清单（纯文档）|
| `docs/design/runtime/embedding.md`                                             | MODIFY | §11 末尾加 §11.9 "分发 package 形态" |
| `docs/roadmap.md`                                                              | MODIFY | "横向工作流"段加一行：跨平台 package 分发；Deferred Backlog Index 加 2 行 |

**只读引用：**
- `scripts/package.sh` — 现有 host package 流程（升级目标）
- `src/toolchain/host/platforms/{ios,android,wasm}/build.sh` — 当前 facade build 流程
- `src/toolchain/host/examples/hello_c/main.c` — C 嵌入 reference 实现
- `docs/spec/archive/2026-05-12-add-platform-*/` — 4 个平台 facade 原始 spec

## Out of Scope

- 实际产 packages（每平台 Phase 1.1–1.4 一个 spec）
- crates.io publish（Rust 嵌入 facade 已决定不做）
- 多 arch 合并 container 包（multi-slice xcframework / multi-ABI AAR）—— **Deferred** 到 Phase 2
- 真正发布到 npmjs / Maven / SwiftPM（Phase 4 release CI）
- workload 模板 / `z42 workload new` CLI（Phase 3）
- 单独的 embedding-c package —— **砍掉**，desktop host package 已含 `native/libz42.a` + `include/`，C 嵌入者直接用 host 包
- `bin/` 内 future `z42-aotcross-<target>` 等工具（占位但不实现）

## Open Questions（已裁决）

下面 9 个 decision 已通过对话裁决；落字到 design.md：

- [x] **D1 mobile/wasm `bin/` empty 处理** → 创建目录 + `README.md` 占位说明 future tools
- [x] **D2 iOS 是 multi-slice container 还是 per-slice flat** → **per-slice flat**（3 个 iOS package，每个 1 slice + 1 单 slice xcframework，SwiftPM 友好）
- [x] **D3 Android 是 multi-ABI container 还是 per-ABI flat** → **per-ABI flat**（4 个 Android package，每个 1 ABI；AAR 不进 per-arch，Phase 2 出 multi-ABI AAR 包）
- [x] **D4 wasm 是否 alias `libz42.wasm`** → 不 alias，保留 `z42_wasm_bg.wasm` 原名 + 新增 `libz42.a` 静态库
- [x] **D5 所有 package 标 `abi-version = 1`** → 加
- [x] **D6 wasm 是否产静态库** → 是（`cargo rustc --crate-type=staticlib`）
- [x] **D7 是否保留 `facade/` 目录层** → **砍**；平台原生入口（Package.swift / Kotlin / package.json）直接放 package root，SwiftPM / npm 一行能用
- [x] **D8 Package 命名约定** → `z42-<v>-<rid>-<config>` per-arch flat，**不带 `<target>` 前缀**（RID 已唯一标识平台）
- [x] **D9 单独的 embedding-c package 是否保留** → **砍掉**，desktop host package 同时是 C 嵌入 SDK；不重复
