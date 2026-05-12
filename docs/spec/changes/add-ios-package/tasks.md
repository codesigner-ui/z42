# Tasks: iOS per-slice SDK package

> 状态：🟡 进行中 | 创建：2026-05-13 | 类型：refactor / build infra

## 进度概览

- [x] 阶段 1: spec 文档
- [ ] 阶段 2: examples/embedding/hello_c/README.md.ios 模板
- [ ] 阶段 3: package_helpers.sh 扩展（pkg_emit_ios_xcframework / pkg_emit_swift_sources / pkg_emit_ios_package_swift）
- [ ] 阶段 4: platforms/ios/build.sh export per-slice package
- [ ] 阶段 5: scripts/package.sh 识别 `--rid ios-*` dispatch
- [ ] 阶段 6: 验证 ios-arm64 + ios-arm64-sim 产包；SwiftPM 一行消费 smoke
- [ ] 阶段 7: README + archive + commit

## 备注

- 不含 ios-x64-sim（memory `project_supported_platforms` 白名单）
- xcframework 内每包单 slice；命名 `Z42VM.xcframework`（不带 slice 后缀，SwiftPM 友好）
- Swift sources cp 进每 slice package（SHA-256 invariant 校）
- 与 add-ios-tests in-repo 流程共存
