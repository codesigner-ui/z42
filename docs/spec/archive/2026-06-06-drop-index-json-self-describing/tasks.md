# Tasks: 以 zpkg NSPC 自描述取代 index.json

> 状态：🟢 已完成（本地可验证部分）| 创建：2026-06-05 | 类型：vm

## 阶段 1: NSPC 读取 helper
- [x] 1.1 `host/config.rs`：`Z42NamespaceVisitor` 类型
- [x] 1.2 `host/mod.rs`：`z42_zpkg_read_namespaces`（复用 `read_zpkg_meta` → 包名 + namespace）
- [x] 1.3 `include/z42_host.h`：typedef + 函数声明
- [x] 1.4 `embed/lib.rs`：`read_zpkg_namespaces()` Rust 包装
- [x] 1.5 `host_tests.rs`：3 个单测（多 namespace / garbage / null visitor）

## 阶段 2: WASM facade
- [x] 2.1 `wasm/src/lib.rs`：wasm-bindgen `readNamespaces` 导出
- [x] 2.2 `wasm/js/stdlib-resolver.js`：读 NSPC 建 mapResolver（保留 zpkgResolver hook）
- [x] 2.3 `wasm/js/index.d.ts` + `tests/host.js`：签名 + 传参
- [x] 2.4 `wasm/build.sh`：生成 `files.json` 取代 index.json

## 阶段 3: iOS / Android facade
- [x] 3.1 iOS `ZpkgResolver.swift`：`BundleZpkgResolver` 枚举 + 读 NSPC
- [x] 3.2 Android `ZpkgResolver.kt`：`AssetZpkgResolver` 枚举 + 读 NSPC
- [x] 3.3 Android `Z42VM.kt` + `z42vm_jni.c`：static `readNamespaces` + JNI 桥
- [x] 3.4 iOS/Android `build.sh`：停拷 index.json；删 Android 签入 asset
- [x] 3.5 README + instrumented test 注释

## 阶段 4: 去 index.json 生成/分发 + 文档
- [x] 4.1 `xtask_stdlib.z42`：删 `_indexJson()`
- [x] 4.2 `xtask_package.z42` / `xtask_test_cross.z42`：停拷
- [x] 4.3 `xtask.z42` / `scripts/README.md` 注释
- [x] 4.4 `embedding.md` §11.7/§11.7.1 + overview/build-artifacts-layout + workflow/* 去 index.json

## 阶段 5: 验证
- [x] 5.1 `cargo build` runtime + embed —— 通过
- [x] 5.2 `cargo test host::` —— 25/25（删 dist/index.json 后仍 25/25）
- [x] 5.3 `cargo test --lib` —— 736 + 21 全绿
- [x] 5.4 `dotnet build` 0 错误 + `dotnet test` 1516/1516
- [ ] 5.5 `z42 xtask.zpkg test vm/cross-zpkg/dist` —— **CI**（本机 z42 未安装）
- [ ] 5.6 mobile/wasm facade 构建 —— **CI**（本机无 Xcode/NDK/wasm-pack）

## 备注
- 关键修正：`z42_zpkg_read_namespaces` 返回**包名 + namespace**——prelude `z42.core` 按包名解析，
  测试暴露纯 NSPC（只有 `Std`）会漏 prelude。
- 加载 hook 全程保留（用户澄清：hook 与 index.json 正交）。
- 中途曾误删 hook，已 `git checkout` 回退。
