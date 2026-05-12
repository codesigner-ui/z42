# Design: iOS per-slice SDK package

## Architecture

```
platforms/ios/build.sh
    ├─ cargo build --target aarch64-apple-ios → libz42-ios-arm64.a
    └─ cargo build --target aarch64-apple-ios-sim → libz42-ios-arm64-sim.a
                          │
                          ▼
scripts/package.sh --rid ios-arm64  ←┐
scripts/package.sh --rid ios-arm64-sim
                          │
                          ▼ pkg_emit_ios_xcframework / pkg_emit_swift_sources
                          ▼ pkg_emit_examples_hello_c (README.md.ios)
                          ▼ pkg_emit_manifest (ios variant)
                          ▼ pkg_sha256_check
                          │
                          ▼
artifacts/packages/
├── z42-<v>-ios-arm64-release/
│   ├── bin/README.md
│   ├── libs/*.zpkg + index.json    ← cross-package byte-identical
│   ├── native/libz42.a + Z42VM.xcframework/ + include/
│   ├── Sources/{Z42VM,Z42VMC}/
│   ├── Package.swift
│   ├── examples/hello_c/{main.c,hello.zbc,README.md}
│   └── manifest.toml
└── z42-<v>-ios-arm64-sim-release/  (same shape, sim slice)
```

## Decisions

### D1: 每 package 内单 slice xcframework（命名 `Z42VM.xcframework`）

每个 per-slice package 内的 xcframework **同名** `Z42VM.xcframework`（不带 slice 后缀）；SwiftPM `.package(path:)` 一行即用。

理由：用户在 Xcode 项目里加多个 path-dep 时，每个 package 独立解析，不会冲突。

### D2: Swift 源 cp 进每个 slice package

`Sources/Z42VM/*.swift` 在每 slice package 都有一份副本（SwiftPM library 需要源码 build；不能引用外部源）。SHA-256 cross-check 保证两份 byte-identical。

### D3: bin/ 占位 README

mobile 无 host CLI；`bin/README.md` 内容固定，说明本目录预留 future cross tools（`z42-aotcross-ios` 等）。

### D4: 与 add-ios-tests / platforms/ios in-repo 测试共存

`platforms/ios/build.sh` 同时支持：
- in-repo 测试（add-ios-tests 用 `Z42VM.xcframework/` 在 platforms/ios/ 内）
- package export（`--export-package` flag 或环境变量）写 `artifacts/packages/`

不破坏现有 add-ios-tests 流程。

## Implementation Notes

- ios slice 的 `libz42.a` 直接复用 `artifacts/build/runtime/<cargo-target>/release/libz42.a`（cargo 已经按 target 分目录）
- single-slice xcframework：`xcodebuild -create-xcframework -library libz42.a -headers include/ -output Z42VM.xcframework`
- iOS deployment target = 14.0（与 platforms/ios/Package.swift 一致）

## Testing Strategy

- `./scripts/package.sh release --rid ios-arm64` 产包；目录结构 + manifest schema + SHA invariant 通过
- 用产出的 ios-arm64 package 在 Xcode 新建空白项目 `.package(path:)` 引用，编译 + 运行 `import Z42VM` 测试代码
- `./scripts/test-all.sh` 6 stage 不退步
- add-ios-tests 的 `swift test` 不退步（in-repo 测试 与 package export 共存验证）
