# Proposal: 以 zpkg NSPC 自描述取代 index.json

## Why

`index.json` 是一张**手维护**的 `namespace → zpkg 文件名` 映射，由
[`scripts/xtask_stdlib.z42`](../../../../scripts/xtask_stdlib.z42) 的 `_indexJson()`
**硬编码**生成。而每个 zpkg 自身的 `NSPC` section 已经权威记录了它声明的 namespace ——
两者构成**第二真相源**，易漂移（实测：旧 `index.json` 声称 `z42.core.zpkg` 提供
`Std.Exceptions`，但其 `NSPC` 实为 `Std` / `Std.Collections`）。这正是
[common-pitfalls §1](../../../../.claude/rules/common-pitfalls.md) 反对的"手维护注册表"。

本变更删除 `index.json`：namespace 归属一律由 zpkg 的 `NSPC` 自描述。**嵌入式加载
hook 保留不变**（宿主提供/注入 zpkg 字节的回调是必要机制，与"如何把 namespace 映射到
字节"是两码事）。

## What Changes

1. **保留**嵌入式加载 hook：`ZpkgResolver` / C `Z42ZpkgResolverFn` / `CHookResolver` /
   `install_zpkg_resolver` / `MapResolver` / `SearchPathsResolver` / iOS `BundleZpkgResolver` /
   Android `AssetZpkgResolver` / WASM `JsCallbackResolver` —— 全部不动。
2. **新增** `z42_zpkg_read_namespaces(bytes, len, visit, user_data)`（C ABI visitor）+
   wasm-bindgen `readNamespaces` 导出 + `Z42VM.readNamespaces`（Android JNI）。返回一个 zpkg
   的**解析键** = 包名（prelude `z42.core` 按包名解析）+ 每个 NSPC namespace。复用 Rust 内部
   `read_zpkg_meta`，让 Swift / Kotlin / JS 不必重写 zpkg 解析。
3. **三个平台默认 resolver** 改为枚举可见 zpkg + 读 NSPC 自建 `namespace → bytes` 表，取代读
   `index.json`：iOS `Bundle.urls`、Android `AssetManager.list`、WASM Node `readdir` / 浏览器
   `files.json`（build 生成的纯文件名清单——HTTP 无法枚举的派生替身，非 namespace 映射）。
4. **删除 index.json 生成 / 分发**：`_indexJson()`、xtask package / test-cross 拷贝、各
   `build.sh` 拷贝、Android 签入 asset。
5. **文档同步**：embedding.md §11.7 / §11.7.1 + 各处去 index.json。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/host/mod.rs` | MODIFY | 新增 `z42_zpkg_read_namespaces`（visitor → 包名 + NSPC namespace） |
| `src/runtime/src/host/config.rs` | MODIFY | 新增 `Z42NamespaceVisitor` 类型 |
| `src/runtime/include/z42_host.h` | MODIFY | 声明 visitor typedef + `z42_zpkg_read_namespaces` |
| `src/runtime/src/host/host_tests.rs` | MODIFY | 新增 helper 单测（多 namespace / garbage / null visitor） |
| `src/toolchain/host/embed/src/lib.rs` | MODIFY | `read_zpkg_namespaces()` Rust 包装（包名 + namespace） |
| `src/toolchain/host/platforms/wasm/src/lib.rs` | MODIFY | wasm-bindgen `readNamespaces` 导出 |
| `src/toolchain/host/platforms/wasm/js/stdlib-resolver.js` | MODIFY | 读 NSPC 建 mapResolver，取代读 index.json |
| `src/toolchain/host/platforms/wasm/js/index.d.ts` | MODIFY | `readNamespaces` 声明 |
| `src/toolchain/host/platforms/wasm/tests/host.js` | MODIFY | 传入 readNamespaces |
| `src/toolchain/host/platforms/wasm/build.sh` | MODIFY | 生成 `files.json` 取代拷 index.json |
| `src/toolchain/host/platforms/ios/Sources/Z42VM/ZpkgResolver.swift` | MODIFY | `BundleZpkgResolver` 枚举 + 读 NSPC |
| `src/toolchain/host/platforms/ios/build.sh` | MODIFY | 停拷 index.json |
| `src/toolchain/host/platforms/ios/README.md` | MODIFY | 去 index.json |
| `src/toolchain/host/platforms/android/z42vm/src/main/java/io/z42/vm/ZpkgResolver.kt` | MODIFY | `AssetZpkgResolver` 枚举 + 读 NSPC |
| `src/toolchain/host/platforms/android/z42vm/src/main/java/io/z42/vm/Z42VM.kt` | MODIFY | static `readNamespaces` external |
| `src/toolchain/host/platforms/android/z42vm/src/main/cpp/z42vm_jni.c` | MODIFY | JNI `readNamespaces` 桥 |
| `src/toolchain/host/platforms/android/z42vm/src/androidTest/java/io/z42/vm/Z42VMInstrumentedTest.kt` | MODIFY | 注释去 index.json |
| `src/toolchain/host/platforms/android/build.sh` | MODIFY | 停拷 index.json |
| `src/toolchain/host/platforms/android/README.md` | MODIFY | 去 index.json |
| `scripts/xtask_stdlib.z42` | MODIFY | 删 `_indexJson()` + 写入 |
| `scripts/xtask_package.z42` | MODIFY | 停拷 index.json |
| `scripts/xtask_test_cross.z42` | MODIFY | 停拷 index.json |
| `scripts/xtask.z42` / `scripts/README.md` | MODIFY | 注释去 index.json |
| `docs/design/runtime/embedding.md` | MODIFY | §11.7 / §11.7.1 改为 resolver 读 NSPC |
| `docs/design/stdlib/overview.md` / `docs/design/compiler/build-artifacts-layout.md` | MODIFY | 去 index.json |
| `docs/workflow/{README,building/stdlib,packaging,windows}.md` | MODIFY | 去 index.json |

## Out of Scope

- **删除嵌入式加载 hook**：明确保留（与本变更正交）。
- **`z42_host_add_zpkg` 注入 API**：不需要——"主动注入"（web playground / REPL）用现有
  `MapResolver`（宿主读 NSPC 填表）经同一 hook 即可。
- **stdlib.lock / 版本钉死 / 完整性**：另列，不在本次。

## Open Questions

- [ ] 无（`z42_zpkg_read_namespaces` 返回包名 + namespace 的语义经测试确认：prelude 按包名
  `z42.core` 解析，必须包含包名键）。
