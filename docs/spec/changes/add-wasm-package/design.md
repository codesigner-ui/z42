# Design: wasm SDK package（staticlib + cdylib + wasm-bindgen）

## Architecture

```
platforms/wasm/build.sh
    ├─ wasm-pack build --target web     → pkg-web/{z42_wasm.js,z42_wasm_bg.wasm}
    ├─ wasm-pack build --target nodejs  → pkg-nodejs/{z42_wasm.js,z42_wasm_bg.wasm}
    └─ cargo rustc --target wasm32-unknown-unknown --crate-type=staticlib
                                        → libz42.a (wasm32 object archive)
                          │
                          ▼
scripts/package.sh --rid wasm32
                          ▼ pkg_emit_wasm_staticlib
                          ▼ pkg_emit_wasm_pkg_dirs (cp pkg-web/ pkg-nodejs/)
                          ▼ pkg_emit_npm_manifest
                          ▼ pkg_emit_examples_hello_c (README.md.wasm)
                          ▼ pkg_emit_manifest (wasm variant)
                          ▼ pkg_sha256_check
                          │
                          ▼
artifacts/packages/z42-<v>-wasm32-<config>/
```

## Decisions

### D1: staticlib + cdylib 都 ship

按 Phase 1.0 D6 决策。`libz42.a` 给手工 wasm-ld 链接用户；`pkg-web/` / `pkg-nodejs/` 给 JS 用户。

### D2: 双 wasm-bindgen target 都装

`pkg-web/` 和 `pkg-nodejs/` 同包内，用户 `import` 时 npm exports map 路由（package.json 已配）。两份合计 ~600KB（wasm 二进制是同 wasm，JS glue 不同）。

### D3: hello_c wasm 不端到端跑

wasm 上 hello_c 端到端需要：
- 编 hello.z42 → wasm32-unknown-unknown obj（需要未来 z42-aotcross-wasm 工具）
- wasm-ld 链 + 运行

当前缺 aotcross。Phase 1 只产 `libz42.a` 与 README 示意命令；端到端 demo 等 aotcross-wasm 出来。

### D4: WASI 不进 Phase 1

wasm32-wasi 也是合法 target（wasmtime / wasmer 用），但当前用户场景是 browser / Node.js + wasm-bindgen。WASI 进 Future Work。

## Implementation Notes

- staticlib emit: `cargo rustc --release --lib --crate-type=staticlib --manifest-path src/runtime/Cargo.toml --target wasm32-unknown-unknown --features wasm`
- `--features wasm` preset = interp-only + 无 libffi / 无 libloading（wasm sandbox 不支持 dlopen）
- 产物：`artifacts/build/runtime/wasm32-unknown-unknown/release/libz42.a`

## Testing Strategy

- `./scripts/package.sh release --rid wasm32` 产包；目录结构 + manifest + SHA invariant
- `file native/libz42.a` 输出含 "current ar archive"；`ar t native/libz42.a` 列内部 .o 文件
- `npm pack` + 其它项目 `npm install` 路径走通
- add-wasm-tests playwright 不退步
