# Tasks: host SDK package conforming to per-arch flat

> 状态：🟢 已完成 | 创建：2026-05-13 | 归档：2026-05-13 | 类型：refactor（基础设施升级）

## 进度概览

- [x] 阶段 1: spec 文档
- [x] 阶段 2: examples/embedding/hello_c + hello_rust source root
- [x] 阶段 3: scripts/_lib/package_helpers.sh 重构
- [x] 阶段 4: scripts/package.sh 升级（RID + manifest schema + staticlib emit + examples + SHA check）
- [x] 阶段 5: 验证 macos-arm64 + macos-x64 cross-compile
- [x] 阶段 6: README / docs + archive + commit

## 阶段 2: examples/embedding/hello_c + hello_rust

- [x] 2.1 `examples/embedding/hello_c/main.c` cp from `src/toolchain/host/examples/hello_c/main.c`
- [x] 2.2 `examples/embedding/hello_c/README.md.host` 模板（host cc link 命令）
- [x] 2.3 `examples/embedding/hello_rust/{Cargo.toml,src/main.rs,README.md}` cp from `src/toolchain/host/examples/hello_rust/`，Cargo.toml path-dep 用 README 占位说明

## 阶段 3: package_helpers.sh

- [x] 3.1 `scripts/_lib/package_helpers.sh` 新建
- [x] 3.2 `detect_host_rid` / `rid_to_cargo` / `rid_to_dotnet` / `validate_rid_supported_on_host`
- [x] 3.3 `pkg_copy_libs` / `pkg_copy_native_includes` / `pkg_emit_examples_hello_c` / `pkg_emit_examples_hello_rust`
- [x] 3.4 `pkg_emit_manifest`（按 R9 完整 schema：[package] / [contents] / [contents.native] / [contents.platform] / [compat]）
- [x] 3.5 `pkg_sha256_check`

## 阶段 4: scripts/package.sh 升级

- [x] 4.1 RID 命名 `osx-*` → `macos-*` / `win-x64` → `windows-x64`
- [x] 4.2 加 `--rid <rid>` flag + validation
- [x] 4.3 强制 `cargo rustc --crate-type=staticlib` + `--crate-type=cdylib` emit
- [x] 4.4 dotnet publish RID 映射
- [x] 4.5 manifest.toml 用 `pkg_emit_manifest` helper
- [x] 4.6 末尾跑 `pkg_sha256_check`
- [x] 4.7 加 `--help` 含 RID 枚举说明

## 阶段 5: 验证

- [x] 5.1 `./scripts/package.sh release` (host RID = macos-arm64) 产包，5 项目齐
- [x] 5.2 验产物结构 + manifest schema + SHA-256 invariant
- [x] 5.3 用产出 host package 跑 examples/hello_c：`cd .../examples/hello_c && cc -I ../../native/include main.c -L ../../native -lz42 -liconv -lSystem -lc -lm -o hello_c && ./hello_c hello.zbc ../../libs/` → `[host] hello, world`
- [x] 5.4 `./scripts/package.sh release --rid macos-x64` 产 x86_64 包，`file native/libz42.a` 含 x86_64
- [x] 5.5 `./scripts/test-all.sh` 6 stage 不退步

## 阶段 6: README + 文档 + 归档

- [x] 6.1 `docs/workflow/release/` 或 `docs/workflow/building/` 新增 / 更新 `packaging.md`
- [x] 6.2 移 `changes/add-host-package-conform/` → `archive/2026-05-13-add-host-package-conform/`
- [x] 6.3 commit + push（type=refactor, scope=build/package）

## 备注

- linux-* / windows-x64 cross-compile from macOS 不在本机验范围；CI matrix 实施
- pkg_sha256_check 在阶段 5 跑当前包内 SHA 与上游 source-of-truth 一致；下游 1.2–1.4 完成后跑跨包 SHA
- `examples/embedding/hello_c/main.c` 与 `src/toolchain/host/examples/hello_c/main.c` 持续 byte-identical（CI gate 检查）；不动 src/ 版本避免破现有 hello_c/build.sh
