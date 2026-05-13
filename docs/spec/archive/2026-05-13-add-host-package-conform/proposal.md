# Proposal: host SDK package 升级到 per-arch flat 契约（add-host-package-conform）

## Why

`define-package-layout` (1.0) 定义 13 个 per-arch flat package 的统一契约。当前 `scripts/package.sh` 产 1 个 host RID 包，形态接近但有 5 处不达标：

1. **RID 命名** `osx-arm64` / `osx-x64`（dotnet 旧惯例）→ 契约 D8 要求 `macos-arm64` / `macos-x64` / `linux-{arm64,x64}` / `windows-x64`
2. **缺 `examples/` 目录** —— R7 要求每包含 `hello_c/{main.c,hello.zbc,README.md}`；desktop 还要 `hello_rust/`
3. **manifest.toml schema 不完整** —— R9 要求 `abi-version` / `[contents]` / `[contents.native]` / `[contents.platform]` / `[compat]` 段
4. **静态库 + 动态库未强制 emit** —— 当前 `cargo build --release` 只产 rlib；libz42.a / libz42.dylib 没自动出（同 hello_c spec 修过的 grandfather bug 同款）
5. **缺 SHA-256 invariant check** —— R3/R4/R7 要求跨 13 包 byte-identical，本 spec 给 1 个 RID 做也要把 SHA gate 引入（同 5 个 RID 之间互查）

## What Changes

`scripts/package.sh` 重构 + 加新 helper `scripts/_lib/package_helpers.sh`。本 spec 落地 5 个 desktop RID 的产包路径（默认当前 host RID 自动验通；cross-RID 走 `--rid` flag 验得动 `macos-arm64↔macos-x64`；linux/windows-x64 cross 暂留 CI 矩阵）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/package.sh`                                                        | MODIFY | RID 命名修正 + manifest schema 升级 + 强制 staticlib/dylib + 加 examples/ + SHA-256 invariant 校验 |
| `scripts/_lib/package_helpers.sh`                                           | NEW    | 共用 helpers：`pkg_emit_manifest` / `pkg_copy_libs` / `pkg_copy_native_includes` / `pkg_sha256_check`（下游 1.2–1.4 复用） |
| `examples/embedding/hello_c/`                                               | NEW (dir) | 共享 fixture root —— 包含 `main.c`（cp 自 `src/toolchain/host/examples/hello_c/main.c`）+ `README.md.host` 模板 |
| `examples/embedding/hello_rust/`                                            | NEW (dir) | `Cargo.toml` + `src/main.rs`（cp 自 `src/toolchain/host/examples/hello_rust/`，path-dep 改为 package-relative）|
| `docs/spec/changes/add-host-package-conform/{proposal,design,tasks}.md`     | NEW    | 本 spec 文档 |
| `docs/spec/changes/add-host-package-conform/specs/host-package/spec.md`     | NEW    | Requirement 列表 |

**只读引用：**

- `docs/spec/archive/2026-05-13-define-package-layout/` — 上游契约
- `src/toolchain/host/examples/hello_c/main.c` + `hello_rust/` — fixture 源
- `src/toolchain/host/examples/hello_c/build.sh` — staticlib emit 模式参考
- `src/runtime/include/{z42_abi,z42_host}.h` — C ABI 头
- `versions.toml` — toolchain pin（host RID 枚举的 source of truth）

## Out of Scope

- iOS / Android / wasm package（Phase 1.2–1.4）
- Linux / Windows cross-compile from macOS（深水区 ——交给 CI matrix；本机只验 macos-arm64 + macos-x64）
- Maven / npmjs / SwiftPM 真实 publish（Phase 4）
- Convenience multi-arch container 包（Deferred）

## Open Questions

- [ ] **macos-arm64 ↔ macos-x64 cross-compile**：rustup target add x86_64-apple-darwin 大家都装；本机能验。dotnet publish 用 `-r osx-x64` 也走通过。一并验入本 spec OK？
- [ ] **`examples/embedding/hello_c/main.c`**：与 `src/toolchain/host/examples/hello_c/main.c` 内容上重复（byte-identical）。是 cp 还是符号链接？我倾向 **cp**（避免 git ln 跨平台问题；build.sh 把 cp 作为契约的一部分）。
- [ ] **`hello_rust/` Cargo path-dep**：现 `src/toolchain/host/examples/hello_rust/Cargo.toml` 引 `z42-host = { path = "../../embed" }`。Package 内的 hello_rust 要改成什么？我倾向 README 说明 "用户自己改 path / 改成 git dep / 改成 publish 后的 crates.io" —— 我们不预设特定 path（package 内 hello_rust 是 reference，不是 ready-to-cargo-run）。
