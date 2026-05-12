# Tasks: 升级 vendored libffi 3.2 → 5.1，在 iOS / Android feature preset 中重启 native-interop

> 状态：🟡 进行中 | 创建：2026-05-10 | 类型：fix（依赖升级，无 API / 语义变化）

**变更说明：** 把 runtime 的 `libffi` 依赖从 3.2 升到 5.1（libffi-sys 2.3 → 4.1），并把 `ios` / `android` feature preset 中之前因汇编兼容问题被去掉的 `native-interop` 重新加回来。

**原因：** 旧 libffi-sys 2.3 内嵌的 libffi 3.4.0 在 iOS arm64 上汇编 `sysv.S` 触发 `CFI advance_loc out of range`；这是 iOS / Android 之前不带 `native-interop` 的根因。libffi-sys 4.1 内嵌的 libffi 3.4.7 已修复该问题，bundled C 构建对 desktop macOS / Linux / iOS / Android 全部干净。两套平台的 native 注册能力（Tier 1 ABI）自此可用。

**文档影响：**
- `src/toolchain/host/platforms/ios/README.md` —— 限制段落：标注 native-interop 已启用
- `src/toolchain/host/platforms/android/README.md` —— 同上
- `src/toolchain/host/platforms/ios/rust/Cargo.toml` —— 注释保持现状（已写明 native-interop 应启用）
- `src/toolchain/host/platforms/android/rust/Cargo.toml` —— 替换"v0.1 ships without native-interop"为"已启用"
- `docs/workflow/building/ios.md` —— 修正 libffi 故障兜底说明（不再需要手工切 bundled 或退掉 native-interop）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/Cargo.toml`                                        | MODIFY | libffi 3.2 → 5.1，去掉 `system` feature，重写注释；`ios` / `android` preset 加回 `native-interop` |
| `src/runtime/Cargo.lock` (隐式)                                 | MODIFY | 由 cargo 重新生成 |
| `src/toolchain/host/platforms/ios/README.md`                    | MODIFY | 限制段落更新 |
| `src/toolchain/host/platforms/ios/rust/Cargo.toml`              | — | 已是正确状态 |
| `src/toolchain/host/platforms/android/README.md`                | MODIFY | 限制段落更新 |
| `src/toolchain/host/platforms/android/rust/Cargo.toml`          | MODIFY | 注释从"暂不含"改为"已启用" |
| `docs/workflow/building/ios.md`                                 | MODIFY | 故障兜底说明 |
| `docs/spec/changes/upgrade-vendored-libffi/tasks.md`            | NEW    | 本文件 |

**只读引用：**
- `src/runtime/src/native/dispatch.rs` — 确认 libffi 5.x API 与 3.x 兼容（`Cif::new` / `Arg::new` / `cif.call` 等）
- `src/runtime/src/native/registry.rs` — 同上
- `/tmp/libffi5-test/` — 上次 spawn 的孤立 probe，验证 libffi 5.1 在 aarch64-apple-ios 干净构建

## 任务清单

- [x] 1.1 编辑 `src/runtime/Cargo.toml`：libffi 3.2 → 5.1，去掉 `system` feature，iOS / Android preset 加回 `native-interop`，重写注释
- [x] 1.2 `cargo build --features native-interop`（host macOS arm64）—— 确认 libffi 5.1 API 与现有代码兼容
- [x] 1.3 `cargo build --no-default-features --features interp-only/ios/android` —— 三个 preset 在 host 上构建
- [x] 1.4 `cargo build --no-default-features --features wasm --target wasm32-unknown-unknown` —— wasm 仍能跳过 libffi
- [x] 1.5 `cargo build --no-default-features --features ios --target aarch64-apple-ios --lib` —— iOS arm64 库 cross-compile（之前的 CFI advance_loc 阻塞已消除）
- [x] 1.6 `cd platforms/ios/rust && cargo build --target {aarch64-apple-ios, aarch64-apple-ios-sim, x86_64-apple-ios} --release` —— 三个 iOS target 释放构建
- [ ] 1.7 Android cross-compile —— 本机无 NDK，跳过；libffi-sys 4.1 在 NDK r25+ 已知兼容，下次 `cargo ndk` 触发时验证
- [x] 1.8 更新 platforms/ios + platforms/android README + Cargo.toml 注释 + workflow/building/ios.md 故障兜底
- [ ] 1.9 GREEN 验证：`dotnet build` + `cargo build`（默认 features）+ `dotnet test` + `./scripts/test-vm.sh`
- [ ] 1.10 commit + push

## 备注

- libffi 5.x 与 3.x 在 `libffi::middle::{Arg, Cif, CodePtr, Type}` API 上**完全兼容**，无需改 dispatch.rs / registry.rs。
- 升级后 `Cargo.lock` 中 `libffi`、`libffi-sys` 两个条目版本上调；下游 lockfile 一致性由 workspace 解析保证。
- Android NDK 在本机环境缺失，相关 cross-compile 不在本迭代验证范围；后续在有 NDK 的 CI / 开发机上首次跑 `platforms/android/build.sh` 时会被覆盖。
