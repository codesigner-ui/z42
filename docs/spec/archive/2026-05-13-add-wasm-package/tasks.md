# Tasks: wasm SDK package（含 staticlib）

> 状态：🟢 已完成 | 创建：2026-05-13 | 归档：2026-05-13 | 类型：refactor / build infra

## 进度概览

- [x] 阶段 1: spec 文档
- [x] 阶段 2: examples/embedding/hello_c/README.md.wasm 模板（wasm-ld 命令示意 + JS 路径）
- [x] 阶段 3: package_helpers.sh 扩展（pkg_emit_wasm_pkg_dirs / pkg_emit_wasm_npm_meta / SHA cross-check）
- [x] 阶段 4: scripts/_lib/package_wasm.sh 流水（cargo rustc staticlib + cp pkg-* + npm meta + manifest）
- [x] 阶段 5: scripts/package.sh `--rid browser-wasm` dispatch（Phase 1.2 已落地）
- [x] 阶段 6: 验证产包；`file libz42.a` 是 ar archive；z42_wasm_bg.wasm 是 wasm binary；SHA invariant pass
- [x] 阶段 7: archive + commit

## 备注

- staticlib + cdylib + wasm-bindgen 双 target 全装（D6）
- libz42.a 4.9M / z42_wasm_bg.wasm 344K
- pkg-web/ + pkg-nodejs/ 来自 platforms/wasm/build.sh 预产；本 pipeline cp 进 package
- js/{index.js,index.d.ts,stdlib-resolver.js} 跨 build byte-identical（SHA-256 校）
- package.json 由 helper emit（pkg-root 相对路径，与 platforms/wasm/js/package.json 的 js/ 相对路径不同）
- WASI（wasm32-wasi）进 Future Work（design.md D4）
- hello_c 端到端跑等 aotcross-wasm 工具（Phase 1 仅 link 命令示意）
- 与 add-wasm-tests in-repo 流程共存
