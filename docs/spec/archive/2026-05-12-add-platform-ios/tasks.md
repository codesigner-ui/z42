# Tasks: Add iOS Platform Scaffold

> 状态：🟢 已完成 | 创建：2026-04-29 / 重写：2026-05-12
> 前置：[`add-zpkg-resolver-hook`](../../archive/2026-05-12-add-zpkg-resolver-hook/) ✅
> 同期参考：[`add-platform-wasm`](../../archive/2026-05-12-add-platform-wasm/)（设计模板 + Stage 0 native-interop gate 已完成）
> Spec 修订：见 [design.md REVISION 2026-05-11](design.md) 段，原稿决策中与 z42-host 不冲突的部分仍生效
>
> 本次实施范围（与原稿差异）：
> - **测试 / XCTest / iOSDemo Xcode 工程 / just / CI 阶段**：用户指示"先不考虑测试流程"，推迟到独立 spec
> - 目录从 `platform/ios/` 迁到 `src/toolchain/host/platforms/ios/`
> - Rust crate 直接 wrap `z42-host`（Tier 2），不再走 `z42_runtime::interp::*`
> - 类名 `Z42Vm` → `Z42VM`
> - ZpkgResolver 协议（`BundleZpkgResolver` 默认实现）

## 进度概览

- [x] 阶段 1: 目录骨架 + Package.swift + Rust Cargo.toml + .gitignore
- [x] 阶段 2: C bridge 模块（modulemap + 转发头）
- [x] 阶段 3: Rust crate（staticlib + rlib，re-export z42_host_*）
- [x] 阶段 4: Swift facade（Z42VM / Z42VMModule / Z42VMEntry / Z42VMValue / Z42VMError + ZpkgResolver 协议 + BundleZpkgResolver）
- [x] 阶段 5: build.sh（cargo build × 3 iOS targets + lipo simulator + xcodebuild create-xcframework + stdlib 复制）
- [x] 阶段 6: README + 文档同步
- [x] 阶段 7: 验证（cargo build × iOS targets + swift build Package.swift）+ commit + push + archive

---

## 阶段 1: 目录骨架

- [x] 1.1 `src/toolchain/host/platforms/ios/` 目录
- [x] 1.2 `Package.swift` —— SwiftPM 5.9 manifest；含 `Z42VM`（library）+ `Z42VMC`（system-module C bridge）；platforms 含 iOS 14+ 与 macOS 13+（开发 / 单测用）
- [x] 1.3 `rust/Cargo.toml` —— crate `z42-platform-ios`，`crate-type = ["staticlib", "rlib"]`，path-deps `z42_vm` (features=ios) + `z42-host`
- [x] 1.4 `rust/src/lib.rs` —— 几乎空：`pub use z42_vm::host::*;` 确保符号导出
- [x] 1.5 `.gitignore` 忽略 `.build/` `target/` `*.xcframework/` `Resources/stdlib/*.zpkg`

## 阶段 2: C bridge 模块

- [x] 2.1 `Sources/Z42VMC/include/module.modulemap`：声明 clang module `Z42VMC` 暴露 `z42_host.h`
- [x] 2.2 `Sources/Z42VMC/include/z42_host.h`：转发到 runtime 公开头（`#include "../../../../../runtime/include/z42_host.h"`）
- [x] 2.3 `Sources/Z42VMC/include/z42_abi.h`：转发同理
- [x] 2.4 `Sources/Z42VMC/dummy.c`：空 C 文件让 SwiftPM 把目录当作 system-module 编（实际链接在 build.sh 完成）

## 阶段 3: Rust binding

- [x] 3.1 `rust/build.rs`（可选）—— 设置 `Z42_SKIP_NATIVE_POC` 让 iOS 目标 cross-compile 不踩 native poc（实际 build.rs 自动 skip 已就位，可省）
- [x] 3.2 验证：`cargo build --target aarch64-apple-ios --manifest-path .../rust/Cargo.toml` 通过

## 阶段 4: Swift facade

- [x] 4.1 `Sources/Z42VM/Z42VM.swift` —— 主 class，构造器接受 `ZpkgResolver` + sink handlers；`loadZbc` / `resolveEntry` / `invoke`；`deinit` 自动 `z42_host_shutdown`
- [x] 4.2 `Sources/Z42VM/Z42VMModule.swift` —— 不透明 handle wrapper
- [x] 4.3 `Sources/Z42VM/Z42VMEntry.swift` —— 同上
- [x] 4.4 `Sources/Z42VM/Z42VMValue.swift` —— enum 含 null / i64 / f64 / bool
- [x] 4.5 `Sources/Z42VM/Z42VMError.swift` —— `enum Z42VMError: Error` 映射 `Z42HostStatus` 全部状态
- [x] 4.6 `Sources/Z42VM/ZpkgResolver.swift` —— `protocol ZpkgResolver` + `struct BundleZpkgResolver`（默认从 `Bundle.main` 读 `<ns>.zpkg`）

## 阶段 5: build.sh

- [x] 5.1 fail-fast 检查 `cargo` + `xcodebuild` + iOS targets 装好
- [x] 5.2 编译 `Resources/stdlib/<demo>.z42` → `.zbc`（如 z42c 可用）
- [x] 5.3 从 `artifacts/z42/libs/*.zpkg` 复制到 `Resources/stdlib/`
- [x] 5.4 `cargo build --release --target aarch64-apple-ios --manifest-path rust/Cargo.toml`
- [x] 5.5 `cargo build --release --target aarch64-apple-ios-sim --manifest-path rust/Cargo.toml`
- [x] 5.6 `cargo build --release --target x86_64-apple-ios --manifest-path rust/Cargo.toml`
- [x] 5.7 `lipo -create` simulator slices（arm64-sim + x86_64-sim）→ universal sim 静态库
- [x] 5.8 `xcodebuild -create-xcframework -library <device-arm64.a> -library <sim-universal.a> -output Z42VM.xcframework`

## 阶段 6: README + 文档同步

- [x] 6.1 `src/toolchain/host/platforms/ios/README.md` —— quick start + API + 限制 + 故障排查
- [x] 6.2 `src/toolchain/host/platforms/README.md` 平台索引行 iOS 状态 → 🟢
- [x] 6.3 `docs/design/runtime/cross-platform.md` iOS 段补 Z42VM 概览
- [x] 6.4 `docs/roadmap.md` L2 Embedding 行加 add-platform-ios

## 阶段 7: 验证

- [x] 7.1 `cargo build --target aarch64-apple-ios --manifest-path .../ios/rust/Cargo.toml` 通过
- [x] 7.2 `cargo build --target aarch64-apple-ios-sim --manifest-path .../ios/rust/Cargo.toml` 通过
- [x] 7.3 `cargo build --target x86_64-apple-ios --manifest-path .../ios/rust/Cargo.toml` 通过
- [x] 7.4 `swift build --package-path src/toolchain/host/platforms/ios`（Package.swift 形态正确）通过
- [x] 7.5 既有 lib 测试不退化（host:: 22/22 + 整体 310+）
- [x] 7.6 `./build.sh` 产出 `Z42VM.xcframework` 含 device + simulator slices（如本机已编 z42c + 装好 xcframework 工具）
- [x] 7.7 commit + push + 归档

---

## 备注

### 推迟的阶段（test 流程相关）

- iOSDemo Xcode 工程 → 独立 spec `add-platform-ios-demo`
- XCTest 套件 → 独立 spec `add-platform-ios-tests`
- just / CI 接入 → 独立 spec

### 与原稿差异

| 项 | 原稿 (2026-04-29) | 修订 (2026-05-12) |
|---|-------------------|--------------------|
| 类名 | `Z42Vm` | `Z42VM` |
| 依赖 | `z42_runtime::interp::Interpreter` | `z42-host` Tier 2 + `z42_host.h` |
| 路径 | `platform/ios/` | `src/toolchain/host/platforms/ios/` |
| API | 自定义 `setStdoutHandler(line: String) / run(entryPoint:)` | `Z42VM` 同形 API + `ZpkgResolver` |
| Demo | iOSDemo SwiftUI 工程 + XCTest | **推迟到独立 spec** |
| CI | just / GitHub Actions macos-14 | **推迟到独立 spec** |
| Stage 0 | — | runtime native-interop gate 在 wasm spec 已完成；iOS feature `ios` **v0.1 暂不含** `native-interop`（libffi-sys 3.4 bundled 在 iOS arm64 上 CFI advance_loc 汇编不兼容）。后续 spec 引入 vendored libffi 后开启 |

### 实施依赖

- ✅ `add-zpkg-resolver-hook`（hook 已就位）
- ✅ runtime `native-interop` feature gate（wasm spec 完成）
- 🛠️ `rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios`（本次实施期装）
- 编译器：`dotnet build src/compiler/z42.slnx` 已就位
