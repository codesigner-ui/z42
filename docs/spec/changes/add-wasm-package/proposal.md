# Proposal: wasm SDK package（wasm32，含 staticlib）

## Why

`define-package-layout` (Phase 1.0) 契约 D6 要求 wasm 端**同时**产 staticlib（`libz42.a` wasm32 object archive）+ cdylib（`z42_wasm_bg.wasm` + wasm-bindgen JS glue）。

当前 `platforms/wasm/build.sh` 只产 wasm-bindgen 路径的 `pkg-{web,nodejs}/`，未产 staticlib。本 spec 让 `scripts/package.sh --rid wasm32` 产 1 个完整 wasm SDK package。

## What Changes

1. `scripts/package.sh` 加 wasm32 handling
2. `scripts/_lib/package_helpers.sh` 扩展：`pkg_emit_wasm_staticlib` / `pkg_emit_wasm_pkg_dirs` / `pkg_emit_npm_manifest` / `pkg_emit_examples_hello_c_wasm`
3. `platforms/wasm/build.sh` 重构：增加 `cargo rustc --target wasm32-unknown-unknown --crate-type=staticlib` 产 `libz42.a`；导出完整 package 到 `artifacts/packages/`
4. `examples/embedding/hello_c/README.md.wasm` —— wasm-ld link 命令（链 libz42.a + hello.wasm.o）
5. SHA-256 invariant 校

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/package.sh`                                                          | MODIFY | `--rid wasm32` dispatch |
| `scripts/_lib/package_helpers.sh`                                             | MODIFY | wasm-specific helpers |
| `src/toolchain/host/platforms/wasm/build.sh`                                  | MODIFY | 加 staticlib emit + export package |
| `examples/embedding/hello_c/README.md.wasm`                                   | NEW    | wasm link 指南 |
| `docs/spec/changes/add-wasm-package/{proposal,design,tasks}.md`               | NEW    | 本 spec |
| `docs/spec/changes/add-wasm-package/specs/wasm-package/spec.md`               | NEW    | Requirement 列表 |

## Out of Scope

- 真发到 npmjs.com `@z42/wasm`（Phase 4）
- WASI target（wasm32-wasi）—— Phase 1 仅 wasm32-unknown-unknown；WASI 留 Future Work

## Open Questions

- [ ] **wasm package 内含 wasm-bindgen 双 target (`pkg-web/` + `pkg-nodejs/`) vs 单 target**：当前 platforms/wasm/build.sh 产两份，package 内也保留两份；用户按 import 路径自选。OK?（我倾向保留双 target）
- [ ] **hello_c on wasm 是否真有 end-to-end demo**：wasm 上 C 嵌入需要先编 hello.z42 → wasm obj → wasm-ld 链入 libz42.a。Phase 1 只验证 libz42.a 能被 wasm-ld 识别即可（不端到端跑）。OK?
