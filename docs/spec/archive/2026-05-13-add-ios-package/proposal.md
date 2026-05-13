# Proposal: iOS SDK package（2 slices）

## Why

`define-package-layout` (Phase 1.0) 契约 + memory `project_supported_platforms` 白名单要求 iOS 端出 2 个 per-slice SDK package：

- `z42-<v>-ios-arm64-<config>` (设备, aarch64-apple-ios)
- `z42-<v>-ios-arm64-sim-<config>` (Apple silicon 模拟器, aarch64-apple-ios-sim)

**不含** `ios-x64-sim`（Intel Mac host 不在维护矩阵；详见 memory `project_supported_platforms`）。

当前 `platforms/ios/build.sh` 产 `Z42VM.xcframework`（多 slice container 形态），但**没有**按 per-arch flat 契约导出。本 spec 让 `scripts/package.sh --rid ios-arm64` / `--rid ios-arm64-sim` 各产一份 per-slice package。

## What Changes

1. `scripts/package.sh` 加 iOS handling：识别 `--rid ios-*`，调 iOS-specific build 流程而非 dotnet publish + cargo
2. `scripts/_lib/package_helpers.sh` 扩展：`pkg_emit_examples_hello_c_ios` 提供 iOS 平台 README link 命令模板
3. `platforms/ios/build.sh` 重构：吐出 per-slice artifacts 到 `artifacts/packages/z42-<v>-ios-<slice>-<config>/`，每个内含 `native/libz42.a` + `native/Z42VM-<slice>.xcframework/`（单 slice）+ `Sources/Z42VM/` + `Package.swift` + `examples/hello_c/{main.c,hello.zbc,README.md}` + `bin/README.md`（占位）+ `libs/` + `manifest.toml`
4. `examples/embedding/hello_c/README.md.ios` 新增 iOS 平台 link 指南
5. SHA-256 invariant：libs/ + native/include/ + examples/hello_c/main.c 与 desktop / Android / wasm package byte-identical

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/package.sh`                                                         | MODIFY | 识别 `--rid ios-*` dispatch 到 iOS 路径 |
| `scripts/_lib/package_helpers.sh`                                            | MODIFY | 加 `pkg_emit_ios_xcframework` / `pkg_emit_swift_sources` / `pkg_emit_ios_package_swift` helpers |
| `src/toolchain/host/platforms/ios/build.sh`                                  | MODIFY | export per-slice 到 `artifacts/packages/` 而非 `platforms/ios/Z42VM.xcframework/` |
| `examples/embedding/hello_c/README.md.ios`                                   | NEW    | iOS 平台 link 指南（`xcrun -sdk iphoneos clang ... -L native -lz42`）|
| `docs/spec/changes/add-ios-package/{proposal,design,tasks}.md`               | NEW    | 本 spec |
| `docs/spec/changes/add-ios-package/specs/ios-package/spec.md`                | NEW    | Requirement 列表 |

**只读引用：**
- `docs/spec/archive/2026-05-13-define-package-layout/` — 上游契约
- `src/toolchain/host/platforms/ios/Sources/Z42VM/*.swift` — facade 源
- memory `project_supported_platforms` — RID 白名单

## Out of Scope

- 多 slice 卷包（multi-slice xcframework convenience）→ Phase 2 Deferred
- iOS x64 sim 包（不在白名单）
- SwiftPM 真发布（Phase 4）
- iOS notarization（unsigned 包；Phase 4 release CI）

## Open Questions

- [x] **iOS slices 数量**：2（arm64 device + arm64 sim），不含 x64 sim → 见 memory
- [ ] **per-slice xcframework 命名**：`Z42VM-ios-arm64.xcframework` vs `Z42VM.xcframework`（让所有 slice 包同名，与 multi-slice 形态共存友好）？我倾向 **Z42VM.xcframework**（每包内单 slice，SwiftPM `.package(path:)` 一行；用户合并多 slice 时手动 lipo）
- [ ] **Swift sources 是 cp 还是 symlink**：每 slice package 内都需要 Sources/Z42VM/*.swift；同 hello_c/main.c 走 cp 路径（SHA-256 invariant 校）
