# Design: 跨平台 SDK package 目录约定（per-arch flat）

## Architecture

```
artifacts/packages/

Desktop SDK (5 — host package = C 嵌入 SDK 同一份)：
├── z42-<v>-macos-arm64-<config>/
├── z42-<v>-macos-x64-<config>/
├── z42-<v>-linux-arm64-<config>/
├── z42-<v>-linux-x64-<config>/
└── z42-<v>-windows-x64-<config>/

iOS (3 — per slice)：
├── z42-<v>-ios-arm64-<config>/         设备
├── z42-<v>-ios-arm64-sim-<config>/     Apple silicon sim
└── z42-<v>-ios-x64-sim-<config>/       Intel sim

Android (4 — per ABI)：
├── z42-<v>-android-arm64-<config>/     arm64-v8a
├── z42-<v>-android-armv7-<config>/     armeabi-v7a
├── z42-<v>-android-x64-<config>/       x86_64
└── z42-<v>-android-x86-<config>/       x86

wasm (1)：
└── z42-<v>-wasm32-<config>/

合计 13 个 per-arch package（release 维度；debug 各自镜像可选）
```

每 package 内部统一：

```
z42-<v>-<rid>-<config>/
├── bin/                      desktop: z42c+z42vm；mobile/wasm: README 占位
├── libs/                     stdlib zpkg + zsym + index.json  ← 跨 package byte-identical
├── native/
│   ├── libz42.{a,dylib,so,dll}         平台静态/动态
│   ├── <container>                     单 slice xcframework (iOS) / .wasm + pkg-{web,nodejs} (wasm)
│   └── include/{z42_abi,z42_host}.h    ← 跨 package byte-identical
├── (root) <平台原生入口>     iOS: Sources/ + Package.swift；Android: kotlin/ + cpp/；wasm: pkg-*/ + package.json
├── examples/
│   ├── hello_c/              main.c byte-identical 跨 package；README 平台特定
│   └── (host) hello_rust/    仅 desktop
└── manifest.toml
```

## Decisions

### D1: mobile / wasm `bin/` 创建目录 + README 占位

mobile / wasm 当前 `bin/` 没东西，**创建空目录 + `README.md`** 显式说明 future tools（如 `z42-aotcross-<target>`）。理由：
- 与 desktop 形态一致；顶层 5 项必备项不变
- 用户看到 README 知道这是预留位

### D2: iOS per-slice flat（3 个 package）

**3 个 iOS package**，每个对应 xcframework 的一个 slice：
- `ios-arm64`（device aarch64-apple-ios）
- `ios-arm64-sim`（aarch64-apple-ios-sim）
- `ios-x64-sim`（x86_64-apple-ios，Apple deprecate 中但仍 ship）

每个 package 内 `native/Z42VM-<slice>.xcframework/` 是**单 slice xcframework**（xcframework 形式合法，可装 1 slice），SwiftPM `.package(path: "...")` 一行可消费。

**Trade-off**：用户要 "device + sim 一并 SwiftPM"得加 3 个 `.package`。multi-slice xcframework 包进 Phase 2 Deferred backlog。

### D3: Android per-ABI flat（4 个 package）

**4 个 Android package**：
- `android-arm64`（arm64-v8a）
- `android-armv7`（armeabi-v7a）
- `android-x64`（x86_64）
- `android-x86`（x86）

每个 package 内 `native/libz42_platform_android.{a,so}` 只一个 ABI；外加 `kotlin/io/z42/vm/*.kt` + `cpp/{z42vm_jni.c,CMakeLists.txt}` 共享源（per-arch 副本）。

**AAR 不在 per-arch package**（AAR 本性是 multi-ABI 容器）。multi-ABI AAR 包进 Phase 2 Deferred。

### D4: wasm 不 alias `libz42.wasm`

wasm-bindgen 强制产 `<crate>_bg.wasm`；rename 会让 JS glue 找不到模块。决定：保留 `z42_wasm_bg.wasm` 原名（cdylib 路径）+ 新增 `libz42.a`（staticlib 路径）。两者并存。

### D5: 所有 package 标 `abi-version = 1`

manifest.toml `[package].abi-version = 1`，匹配 `Z42_HOST_ABI_VERSION`。未来升 2 时所有 package 同步升；旧包拒被新 host link。

### D6: wasm 产静态库

`cargo rustc --target wasm32-unknown-unknown --crate-type=staticlib --features wasm` 产 `libz42.a`（wasm32 object archive）。可被 `wasm-ld` 链入更大 wasm 模块；与桌面 / mobile 静态库形态对称。

### D7: 砍掉 `facade/` 层

**平台原生消费入口直接放 package root**，不嵌套 `facade/`：

| target | root 入口文件 |
|--------|--------|
| desktop | （无；C 用户直接用 `native/include/`）|
| iOS | `Sources/Z42VM/*.swift` + `Package.swift` |
| Android | `kotlin/io/z42/vm/*.kt` + `cpp/{z42vm_jni.c,CMakeLists.txt}` |
| wasm | `pkg-web/` + `pkg-nodejs/` + `js/*.js` + `package.json` |

**理由**：SwiftPM / npm 期望 manifest 在 package root；嵌套 `facade/` 让 `.package(path: ".../facade")` 多一层；用户认知负担没必要。

### D8: Per-arch flat 命名（不带 `<target>` 前缀）

`z42-<version>-<rid>-<config>`，RID 完全标识平台 + 架构：

- `macos-arm64` / `macos-x64` / `linux-arm64` / `linux-x64` / `windows-x64` — desktop
- `ios-arm64` / `ios-arm64-sim` / `ios-x64-sim` — iOS slices
- `android-arm64` / `android-armv7` / `android-x64` / `android-x86` — Android ABIs
- `wasm32` — wasm

**好处**：扁平易扫；package 列表直接看 RID 一眼知道哪个平台 + 哪个 arch；与 .NET / cargo RID convention 一致。

### D9: 砍单独的 embedding-c package

之前 Phase 1.5 = `add-embedding-c-package`，单独一个 spec。但 host package 已含 `bin/z42c + z42vm` + `native/libz42.{a,dylib,so,dll}` + `include/` + `examples/hello_c/`：

- 桌面 C 嵌入者拿 host package 一份即可（额外多 `bin/z42c` 不碍事，他可以无视）
- 不重复内容；不用同步两份 spec

**结论**：Phase 1 = 4 个 platform spec（host + ios + android + wasm），不含独立 embedding-c。

## Cross-cutting concerns

### Byte-identical 保证机制

实施期通过 SHA-256 check 验证（每个下游 spec 阶段 6 GREEN gate 加一步）：

```bash
# 跨 13 个 package 一致性
for d in artifacts/packages/z42-*-release; do
    sha256sum "$d/libs/index.json" "$d/native/include/z42_abi.h" "$d/native/include/z42_host.h"
done | awk '{print $1}' | sort -u
# 期望: 每个文件只 1 个 SHA（13 行 → 3 unique SHA）
```

如不一致 = 某 platform 的 build.sh 改了"应该一致"的内容 = 实施 bug。

### 兼容性矩阵 (Phase 1 v1)

`[compat].host-min-version` 字段提供基础检查 —— ios package 0.1.0 要求 host 工具链 ≥ 0.1.0。Phase 1 不做"ios 0.1 vs host 0.2 兼容矩阵"。abi-version 相同 + version 满足 host-min-version 即视为兼容。

复杂"哪个 abi 字段哪个 version 起加" 矩阵进 Deferred。

## Deferred / Future Work

### multi-arch-container-packages: 多 slice xcframework / 多 ABI AAR 卷起来发

- **来源**：本 spec 草稿期 D2/D3 抉择
- **触发原因**：Phase 1 选 per-arch flat（13 个独立包）；SwiftPM 用户要 device+sim+macos 得装 3+ package；Gradle 用户要 multi-ABI AAR 不存在
- **触发条件**：用户呼声 / Phase 2 实施
- **当前 workaround**：手动下载多个 per-arch 包再组合；本地 build.sh 仍产 xcframework / AAR 用于测试
- **解决方案**：Phase 2 加 `z42-<v>-ios-xcframework-<config>/` + `z42-<v>-android-aar-<config>/` 两个 convenience 包；内部 cp / lipo 已有 per-arch 产物

### per-arch-abi-feature-matrix

- **来源**：本 spec cross-cutting concerns
- **触发原因**：v0.1 abi-version=1 是单值；未来 `Z42HostConfig` 加字段时需要 "哪些字段哪个 ABI 起可用" 细粒度矩阵
- **触发条件**：abi-version 升到 2

### binary-package-signing

- **来源**：本 spec
- **触发原因**：iOS xcframework / Android AAR / wasm npm 有 publish 时签名要求（notarization / GPG / npm 2FA）；Phase 1 全 unsigned
- **触发条件**：Phase 4 release CI

## Implementation Notes

### `manifest.toml` 平台特定字段

- iOS package：`[compat].ios-deployment-target = "14.0"`；`[contents.platform].swiftpm-manifest = "Package.swift"` + `swift-sources = "Sources/Z42VM"`
- Android package：`[compat].android-min-sdk = 23` + `target-sdk = 34`；`[contents.platform].kotlin-sources = "kotlin/io/z42/vm"`
- wasm package：`[compat].wasm-bindgen-version = "0.2"`；`[contents.platform].npm-manifest = "package.json"` + `wasm-bindgen = ["pkg-web", "pkg-nodejs"]`
- desktop package：`[contents.platform]` 段全 empty / 不出现

### per-arch RID 与 `versions.toml` 对应

`versions.toml`（实施期已存在）描述 toolchain pinned versions；本 spec 的 RID 枚举应与 `versions.toml [platform.<target>].rust_targets / abis` 对应。下游 1.x spec 实施时从 versions.toml 取，避免硬编码 RID。

## Testing Strategy

本 spec **不带任何代码改动**，因此无可执行 test。验证：

- 评审：4 份 spec doc + 2 处 design doc 语义自洽
- 形式：Requirement 1–9 在下游 4 个 spec 的 tasks.md 中都能找到对应 task
- 同步：embedding.md §11.9 + roadmap.md Deferred Backlog Index 加索引
