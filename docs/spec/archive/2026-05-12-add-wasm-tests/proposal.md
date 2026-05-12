# Proposal: 给 wasm facade 加 playwright 测试（add-wasm-tests）

## Why

wasm facade 落地（`2026-05-12-add-platform-wasm`）时把 e2e 测试推迟到独立 spec。本 spec 按照 [`platform-test-contract`](../../archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md) R1–R7 实现 wasm facade 的自动化测试集，跟 `add-ios-tests` / `add-android-tests`（待） 平行对齐。

实施期同时**顺手修复 wasm 默认 resolver 的 namespace mapping bug** —— `js/stdlib-resolver.js` 的 `bundleStdlibNode` / `bundleStdlibBrowser` 当前用 package-name 作 map key（`z42.core` / `z42.io` / ...），runtime 问 `Std.IO` 时直接 miss。这与 iOS / Android 之前的 bug 同形态，统一解法是 `stdlib/index.json` 反查表（详见 [`fix-bundle-resolver-namespace-index`](../../archive/2026-05-12-fix-bundle-resolver-namespace-index/)），那个 spec 显式 deferred 了 wasm；本 spec 收回这条债。

落地后效果：`./scripts/install-node-local.sh && cd src/toolchain/host/platforms/wasm && ./build.sh && ./test.sh` 跑通 7 个 contract scenario；终端用户 `import { Z42VM } from '@z42/wasm'` 装 default resolver 后直接能用 `Std.IO` namespace。

## What Changes

1. **`scripts/install-node-local.sh`** —— 已在本 spec 草稿期落地，下载 Node v22 LTS 到 `artifacts/tools/node/`，不污染系统 PATH（已 commit-ready）
2. **`platforms/wasm/js/stdlib-resolver.js`** —— `mapResolver` 改为 index 优先 + 文件名 fallback；`bundleStdlibNode` / `bundleStdlibBrowser` 先 fetch / read `stdlib/index.json`，构建 namespace → bytes 真映射
3. **`platforms/wasm/build.sh`** —— 修 stale path（`artifacts/z42/libs/` → `artifacts/build/libs/release/`；`artifacts/compiler/...` → `artifacts/build/compiler/...`）；用 local Node；同时拷 index.json + 编 test fixtures
4. **`platforms/wasm/tests/`**（新）—— playwright 配置 + R1–R7 e2e（在 headless Chromium 里加载本地 dist + 跑测）
5. **`platforms/wasm/demo/fixtures/hello.z42`**（删 / 重写）—— 改用 `examples/embedding/hello.z42`（与 iOS XCTest 共享 fixture）
6. **平台 README + `js/package.json` `_comment`** 同步描述

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/host/platforms/wasm/js/stdlib-resolver.js`             | MODIFY | 引入 index 优先 + 文件名 fallback；`bundleStdlibNode` / `bundleStdlibBrowser` 读取 index.json |
| `src/toolchain/host/platforms/wasm/build.sh`                          | MODIFY | 修 stale path + 改 local Node + 拷 index.json + 编 test fixtures（hello + multi_line） |
| `src/toolchain/host/platforms/wasm/tests/playwright.config.ts`        | NEW    | playwright 配置（headless chromium、PLAYWRIGHT_BROWSERS_PATH） |
| `src/toolchain/host/platforms/wasm/tests/package.json`                | NEW    | playwright + http-server npm 依赖（本地 node_modules） |
| `src/toolchain/host/platforms/wasm/tests/index.html`                  | NEW    | playwright 加载的"测试 host page" |
| `src/toolchain/host/platforms/wasm/tests/host.js`                     | NEW    | 在 index.html 里跑的 driver（把 R1–R7 暴露给 playwright） |
| `src/toolchain/host/platforms/wasm/tests/r1-r7.spec.ts`               | NEW    | playwright test cases 实现 R1–R7 |
| `src/toolchain/host/platforms/wasm/test.sh`                           | NEW    | 入口：起静态服 → 启 playwright → 关服 |
| `src/toolchain/host/platforms/wasm/.gitignore`                        | NEW/MODIFY | 加 `tests/node_modules/` + `tests/test-results/` + `tests/playwright-report/` |
| `src/toolchain/host/platforms/wasm/demo/fixtures/hello.z42`           | DELETE | 改用共享 `examples/embedding/hello.z42` |
| `src/toolchain/host/platforms/wasm/demo/fixtures/`                    | DELETE (dir if empty) | 编完 hello.zbc 改写到 `js/fixtures/` |
| `src/toolchain/host/platforms/wasm/demo/node/run.js`                  | MODIFY | 入口 FQN `Wasm.Hello.Main` → `Hello.Main`；fixture 路径改 `js/fixtures/hello.zbc` |
| `src/toolchain/host/platforms/wasm/demo/web/run.js`                   | MODIFY | 同上 |
| `src/toolchain/host/platforms/wasm/README.md`                         | MODIFY | 加 "Run tests" 段 + Limitations 段移 "tests: 推迟" |
| `src/toolchain/host/platforms/wasm/js/package.json`                   | MODIFY | `_comment` 更新 stdlib 路径；files 包含 `stdlib/index.json` |
| `scripts/install-node-local.sh`                                       | NEW    | 一次性 Node v22 LTS 装到 `artifacts/tools/node/` |
| `docs/spec/changes/add-wasm-tests/{proposal,design,tasks}.md`         | NEW    | 本 spec 文档 |
| `docs/spec/changes/add-wasm-tests/specs/wasm-tests/spec.md`           | NEW    | R1–R7 in playwright |
| `docs/design/runtime/embedding.md`                                    | MODIFY | §11.7 wasm 行从 "（index 支持随 wasm spec 落地）" 改为 "BundleNodeResolver / BundleBrowserResolver 读 stdlib/index.json"

**只读引用：**

- `examples/embedding/{hello,multi_line}.z42` — 共享 fixture（iOS XCTest 也用）
- `docs/spec/archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md` — R1–R7 契约
- `docs/spec/archive/2026-05-12-fix-bundle-resolver-namespace-index/` — index.json 设计参考（同款修法）
- `src/toolchain/host/platforms/wasm/src/lib.rs` / `resolver.rs` — wasm-bindgen 桥（不动）

## Out of Scope

- iOS XCTest `xcodebuild test -destination iOS Simulator`（另起 `add-ios-ci` spec）
- Android JUnit instrumented test（`add-android-tests`，等 SDK / NDK）
- wasm runtime API 变更（v0.1 surface 不动）
- web demo 的 UI/UX 升级（demo 是 example，不是测试）
- 把 wasm tests 接进 `./scripts/test-all.sh` 默认 GREEN（手动跑或 `--with-wasm` 旗标后续考虑）

## Open Questions

- [ ] **playwright 跑 chromium / WebKit / Firefox 三选哪几个**：iOS 端 swift test 已经覆盖 macOS arm64 facade glue；wasm 端关心的是 browser-side JS / wasm 互操作。Chromium 一份覆盖 V8 + Liftoff/TurboFan；WebKit 覆盖 Apple JSC。我倾向 **chromium-only 起步**（download 体积 ~280MB），WebKit 留 backlog；Firefox 单纯增加体积无 marginal 测试价值。
- [ ] **测试 host page 形态**：playwright 加载本地 `tests/index.html`，那个页面用 `import` 读 `pkg-web/` + `js/stdlib-resolver.js`，然后把 `runScenario(R)` 暴露到 `window.__test`，由 playwright spec 调用。OK？还是直接在 playwright spec 里 page.evaluate 写所有逻辑（无 `host.js`）？我倾向 host.js + spec 拆分，spec 文件更纯。
- [ ] **`./scripts/test-all.sh` 集成时机**：本 spec 先不加（保持 6 stage GREEN 不变）；将来 `--with-wasm` flag 单独 spec。OK？
