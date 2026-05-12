# Spec: BundleZpkgResolver / AssetZpkgResolver namespace index

## ADDED Requirements

### Requirement 1: stdlib build 产 namespace index

`scripts/build-stdlib.sh` 在产生 flat view (`artifacts/build/libs/<profile>/`) 同步生成 `index.json`，列出所有可解析的 namespace → zpkg 文件名映射。

#### Scenario: build-stdlib 产 index.json
- **WHEN** `./scripts/build-stdlib.sh` 跑完
- **THEN** `artifacts/build/libs/release/index.json` 存在，是合法 JSON，键集合至少含 `z42.core` / `Std` / `Std.IO` / `Std.Math` / `Std.Text` / `Std.Collections` / `Std.Test`，每个值是 `artifacts/build/libs/release/` 中真实存在的 `.zpkg` 文件名

### Requirement 2: BundleZpkgResolver 支持 index 优先

iOS `BundleZpkgResolver` 在解析 namespace 时优先用 index 查文件名，回退到原 namespace-as-filename 行为。

#### Scenario: index 在场，按 index 解析
- **WHEN** test bundle 含 `stdlib/index.json` 映射 `{"Std.IO": "z42.io.zpkg", ...}` + `stdlib/z42.io.zpkg`，构造 `BundleZpkgResolver(bundle: .module, subdirectory: "stdlib")` 并调 `resolve(namespace: "Std.IO")`
- **THEN** 返回 `z42.io.zpkg` 的字节

#### Scenario: index 在场但 namespace 不在 index
- **WHEN** index 不含 `Custom.Foo`，但 `stdlib/Custom.Foo.zpkg` 存在
- **THEN** resolver 回退到 namespace-as-filename，返回 `Custom.Foo.zpkg` 的字节

#### Scenario: index 缺失，纯文件名 fallback
- **WHEN** 没有 `stdlib/index.json`，但 `stdlib/Custom.Foo.zpkg` 存在
- **THEN** resolver 返回 `Custom.Foo.zpkg` 的字节（向后兼容自定义 resolver 摆法）

### Requirement 3: AssetZpkgResolver 同步行为

Android `AssetZpkgResolver` 与 iOS 镜像：index 优先 + fallback。

#### Scenario: Android index 在场，按 index 解析
- **WHEN** `assets/stdlib/index.json` 存在 + `assets/stdlib/z42.io.zpkg` 存在，构造 `AssetZpkgResolver(assets, "stdlib")` 并调 `resolve("Std.IO")`
- **THEN** 返回 `z42.io.zpkg` 的字节

### Requirement 4: 平台 build.sh 同步拷 index.json

iOS 和 Android 的 build.sh 都把 `index.json` 拷到对应 bundle / asset 目录。

#### Scenario: iOS build.sh 拷 index.json 到 Resources/stdlib + Tests/...
- **WHEN** `./build.sh`（在 iOS 平台目录）跑完
- **THEN** `Resources/stdlib/index.json` 和 `Tests/Z42VMTests/Resources/stdlib/index.json` 同时存在，且内容一致

#### Scenario: Android build.sh 拷 index.json 到 assets/stdlib
- **WHEN** `./build.sh`（Android）跑完
- **THEN** `z42vm/src/main/assets/stdlib/index.json` 存在

### Requirement 5: R1 / R6 / R7 在 iOS XCTest 下通过

修完 resolver，`add-ios-tests` 的 R1 / R6 / R7 即可通过（已经写好但当前 RED 的 3 个测试）。

#### Scenario: iOS XCTest 全 7 个绿
- **WHEN** `cd src/toolchain/host/platforms/ios && ./build.sh && swift test`
- **THEN** 7 个 Z42VMTests 全部通过

## MODIFIED Requirements

### Requirement: embedding.md §11 resolver 协议补 namespace index 段

**Before:** §11 描述 resolver 接受 namespace 字符串、返回 bytes / null；没说 namespace 与 zpkg 文件名的关系。

**After:** §11 补一段："默认 mobile / wasm resolver 通过 stdlib 目录中的 `index.json` 把 namespace 解析为具体 .zpkg 文件名；index 由 host `build-stdlib.sh` 产出。自定义 resolver 可不读 index，直接维护自己的 namespace → bytes 映射。"

## Pipeline Steps

不涉及编译器 pipeline。
