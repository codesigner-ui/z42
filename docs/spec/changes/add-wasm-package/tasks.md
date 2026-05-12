# Tasks: wasm SDK package（含 staticlib）

> 状态：🟡 进行中 | 创建：2026-05-13 | 类型：refactor / build infra

## 进度概览

- [x] 阶段 1: spec 文档
- [ ] 阶段 2: examples/embedding/hello_c/README.md.wasm 模板（wasm-ld 命令示意）
- [ ] 阶段 3: package_helpers.sh 扩展（pkg_emit_wasm_staticlib / pkg_emit_wasm_pkg_dirs / pkg_emit_npm_manifest）
- [ ] 阶段 4: platforms/wasm/build.sh 加 staticlib emit + export package
- [ ] 阶段 5: scripts/package.sh `--rid wasm32` dispatch
- [ ] 阶段 6: 验证产包；`file libz42.a` + `npm pack` smoke
- [ ] 阶段 7: README + archive + commit

## 备注

- staticlib + cdylib + wasm-bindgen 双 target 全装（D6）
- hello_c 端到端跑等 aotcross-wasm（Phase 1 仅 link 命令示意，不端到端）
- WASI 进 Future Work
- 与 add-wasm-tests in-repo 流程共存
