# Spec: wasm facade playwright tests (wasm-tests)

> 实现 [`platform-test-contract`](../../../../archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md) R1–R7。本 spec 只描述 wasm / playwright 落地形态；scenario 语义见 contract spec。

## 平台执行环境

- **Browser**：headless Chromium（v1.x via playwright）
- **Node**：`artifacts/tools/node/bin/node`（`scripts/install-node-local.sh` 装的本地副本）
- **Static server**：playwright 内置 `webServer` 启 `http-server` 服 `tests/` + `js/` + `pkg-web/`
- **Browser cache**：`artifacts/tools/playwright-browsers/`（通过 `PLAYWRIGHT_BROWSERS_PATH`）

WebKit / Firefox 起步不跑（Open Question Q1）。

## scenario → playwright test 映射

| 契约 # | playwright test 名 | 实现要点 |
|--------|------------------|---------|
| R1 | `smoke / hello world` | host page load hello.zbc → `vm.invoke(Hello.Main)` → captured stdout == `"hello, world\n"` |
| R2 | `error / bad zbc` | `vm.loadZbc(Uint8Array([0xDE, 0xAD, 0xBE, 0xEF]))` → throws Error with `.status == 10` |
| R3 | `error / unknown entry` | `vm.resolveEntry(mod, "App.Ghost")` → throws `.status == 20`，消息含 `App.Ghost` |
| R4 | `error / arg mismatch` | `vm.invoke(entry, 42)` 给 `Hello.Main` → throws `.status == 21` |
| R5 | `resolver miss surfaces` | `mapResolver(new Map([["Std.Phantom", new Uint8Array()]]))` → load/invoke 抛 status 10 或 30 |
| R6 | `lifecycle / repeat init` | 3 轮 `new Z42VM(...).dispose()` + R1 smoke 各跑一次都成功 |
| R7 | `stdout / multi-line order` | load multi_line.zbc → 累积字节 == `"a\nb\nc\n"` |

## ADDED Requirements

### Requirement 1: wasm 默认 resolver 解析 stdlib namespace（修 namespace mapping bug）

`bundleStdlibNode` / `bundleStdlibBrowser` 必须能解析 `Std.IO` / `Std.Math` 等 namespace（而非仅 package name `z42.io` / `z42.math`），通过 `stdlib/index.json` 反查表。

#### Scenario: bundleStdlibNode 解析 Std.IO
- **WHEN** Node 环境，`stdlib/` 含 `index.json` 与 zpkg 文件，调 `(await bundleStdlibNode())("Std.IO")`
- **THEN** 返回 `z42.io.zpkg` 的字节

#### Scenario: bundleStdlibBrowser 解析 Std.IO via fetch
- **WHEN** browser 环境，`stdlib/index.json` 与 zpkg 可 fetch，await `bundleStdlibBrowser(stdlibUrl)` 拿到 resolver，调 `resolver("Std.IO")`
- **THEN** 返回 `z42.io.zpkg` 的字节

#### Scenario: index 缺失时 fallback to filename
- **WHEN** `stdlib/index.json` 不存在但 `stdlib/Custom.Foo.zpkg` 存在，调 `resolver("Custom.Foo")`
- **THEN** 返回 `Custom.Foo.zpkg` 字节（向后兼容自定义 resolver 摆放方式）

### Requirement 2: playwright 跑通 R1–R7

`./test.sh` 在 headless chromium 中跑通全部 7 个 contract scenario。

#### Scenario: test.sh 全 7 个绿
- **WHEN** `./scripts/install-node-local.sh && cd platforms/wasm && ./build.sh && ./test.sh`
- **THEN** playwright 退出码 0，stdout 含 `7 passed`

### Requirement 3: build.sh artifact 路径同步 redesign-artifact-layout

stale path bug 修复 —— wasm/build.sh 引用 `artifacts/build/libs/release/` + `artifacts/build/compiler/`，与其它 build.sh 对齐。

#### Scenario: build.sh references current artifact paths
- **WHEN** 读 build.sh
- **THEN** `LIBS_DIR` 指向 `artifacts/build/libs/release`；`Z42C` 路径含 `artifacts/build/compiler/`

### Requirement 4: build.sh emit test fixtures + index.json

`./build.sh` 产 wasm pkg + stdlib（含 index.json）+ test fixtures 三套产物。

#### Scenario: build.sh complete output
- **WHEN** `./build.sh`
- **THEN** 产物路径含：
  - `pkg-web/z42_wasm.js` + `z42_wasm_bg.wasm`
  - `pkg-nodejs/z42_wasm.js` + `z42_wasm_bg.wasm`
  - `js/stdlib/*.zpkg` + `js/stdlib/index.json`
  - `js/fixtures/hello.zbc` + `js/fixtures/multi_line.zbc`

## MODIFIED Requirements

### Requirement: wasm README Limitations 段移除 "Tests: 推迟"

**Before:** README Limitations 段含 "测试 / CI: 推迟到独立 spec"

**After:** Tests 在本 spec 落地；Limitations 只保留 "CI 集成: 推迟"

## Pipeline Steps

不涉及编译器 pipeline。
