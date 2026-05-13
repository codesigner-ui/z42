# Spec: browser-wasm SDK package（含 staticlib + cdylib）

> 落地 1 个 wasm package：`z42-<v>-browser-wasm-<config>` —— platform = browser（含 Node.js，与 browser 同走 V8 JS host）；arch = wasm32。同时含 staticlib（libz42.a wasm32 object archive）+ cdylib（z42_wasm_bg.wasm + JS glue）。WASI（`wasi-wasm` RID）进 Future Work。

## ADDED Requirements

### Requirement 1: browser-wasm package 产物

#### Scenario: --rid browser-wasm 产包
- **WHEN** `./scripts/package.sh release --rid browser-wasm`
- **THEN** `artifacts/packages/z42-<v>-browser-wasm-release/` 存在，含 bin/libs/native/examples/manifest.toml 5 项 + 平台原生入口

### Requirement 2: 包内结构

```
z42-<v>-browser-wasm-<config>/
├── bin/README.md                        wasm 无 host CLI
├── libs/                                stdlib zpkg + index.json
├── native/
│   ├── libz42.a                         wasm32 object archive (staticlib)
│   ├── z42_wasm_bg.wasm                 cdylib (wasm-bindgen 产)
│   └── include/{z42_abi,z42_host}.h
├── pkg-web/                             wasm-bindgen web target
├── pkg-nodejs/                          wasm-bindgen nodejs target
├── js/{index.js,index.d.ts,stdlib-resolver.js}
├── package.json                         npm publish-ready
├── examples/hello_c/{main.c,hello.zbc,README.md}
└── manifest.toml
```

#### Scenario: native/libz42.a is wasm32 archive
- **WHEN** Phase 1.4 完成
- **THEN** `file native/libz42.a` 输出含 "current ar archive" 且内部 .o 是 wasm32 object format

#### Scenario: pkg-web + pkg-nodejs 完整 wasm-bindgen 产物
- **WHEN** Phase 1.4 完成
- **THEN** `pkg-web/z42_wasm.js` + `pkg-web/z42_wasm_bg.wasm` 都存在；pkg-nodejs/ 同

### Requirement 3: package.json npm-consumable

#### Scenario: npm install <path> 能装
- **WHEN** 用户 `cd <wasm-package>/ && npm pack` 产 tarball
- **THEN** 该 tarball 能在其它项目 `npm install ./z42-wasm-...tgz`；`import { Z42VM } from "@z42/wasm"` 工作

### Requirement 4: hello_c wasm link 示例

`examples/hello_c/README.md` 含 wasm-ld 链接命令示意（可不端到端跑，但命令完整）。

#### Scenario: hello_c wasm README 含 wasm-ld 命令
- **WHEN** 读 `z42-<v>-browser-wasm-release/examples/hello_c/README.md`
- **THEN** 含 `wasm-ld native/libz42.a hello.wasm.o -o app.wasm ...` 示意

### Requirement 5: SHA-256 invariant

#### Scenario: SHA check pass
- **WHEN** `pkg_sha256_check`
- **THEN** libs/ + native/include/ + examples/hello_c/main.c 与所有其它 package SHA 相同

### Requirement 6: manifest.toml wasm 字段

#### Scenario: wasm manifest 完整
- **WHEN** 读 `z42-<v>-browser-wasm-release/manifest.toml`
- **THEN** `[package].rid = "browser-wasm"` + `[contents.platform]` 含 `npm-manifest = "package.json"` + `wasm-bindgen = ["pkg-web", "pkg-nodejs"]` + `[compat].wasm-bindgen-version = "0.2"`

## MODIFIED Requirements

### Requirement: platforms/wasm/build.sh 产物位置

**Before:** in-repo `pkg-{web,nodejs}/` + `js/stdlib/` 产物（add-wasm-tests 用）

**After:** 保留 in-repo；附加导出 browser-wasm package 到 `artifacts/packages/`。staticlib 是新增产物（之前未产）。

## Pipeline Steps

不涉及编译器 pipeline。
