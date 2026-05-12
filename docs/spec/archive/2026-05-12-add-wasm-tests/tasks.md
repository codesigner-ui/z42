# Tasks: 给 wasm facade 加 playwright 测试 + 修 resolver

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：test + fix（contract 实现 + 顺手收 fix-bundle-resolver-namespace-index 的 wasm deferred 债）

## 进度概览

- [x] 阶段 1: 本地 Node + resolver 改造
- [x] 阶段 2: build.sh 修复（stale path + index.json + fixture 编译 + 本地 Node）
- [x] 阶段 3: tests/ playwright 脚手架 + R1–R7 实现
- [x] 阶段 4: 验证 + GREEN
- [x] 阶段 5: README + 归档

## 阶段 1: 本地 Node + resolver 改造

- [x] 1.1 新增 `scripts/install-node-local.sh`；下载 Node v22 LTS 到 `artifacts/tools/node/`
- [x] 1.2 跑 `./scripts/install-node-local.sh` 装 + 验证 `node --version` / `npm --version`
- [x] 1.3 `platforms/wasm/js/stdlib-resolver.js` 改造：`bundleStdlibNode` / `bundleStdlibBrowser` 读 `stdlib/index.json` 构 namespace → bytes 真映射；缺 index 时 fallback to filename；`mapResolver` 通用化
- [x] 1.4 `js/package.json` `_comment` 更新；`files` 含 `stdlib/index.json`
- [x] 1.5 `STDLIB_NAMES` 加 `z42.text`（之前漏的）

## 阶段 2: build.sh 修复

- [x] 2.1 `platforms/wasm/build.sh` 修 stale `artifacts/z42/libs/` → `artifacts/build/libs/release/`
- [x] 2.2 `build.sh` 修 stale `artifacts/compiler/...` → `artifacts/build/compiler/...`
- [x] 2.3 `build.sh` 用 `$REPO/artifacts/tools/node/bin/...` 而非 PATH 上的 node
- [x] 2.4 `build.sh` 拷 `index.json` 到 `js/stdlib/`
- [x] 2.5 `build.sh` 编 `examples/embedding/hello.z42` → `js/fixtures/hello.zbc`
- [x] 2.6 `build.sh` 编 `examples/embedding/multi_line.z42` → `js/fixtures/multi_line.zbc`
- [x] 2.7 移 `demo/fixtures/` 到 `js/fixtures/`；删旧 `demo/fixtures/hello.z42`
- [x] 2.8 改 `demo/node/run.js` 入口 FQN `Wasm.Hello.Main` → `Hello.Main`；fixture 路径 `js/fixtures/hello.zbc`
- [x] 2.9 改 `demo/web/run.js` 同步
- [x] 2.10 跑 `./build.sh`，确认 6 产物 + 1 index.json + 2 fixture 都在

## 阶段 3: tests/ playwright 脚手架

- [x] 3.1 `tests/package.json`：scripts (`test`, `install-browsers`) + devDeps (`@playwright/test`, `http-server`)
- [x] 3.2 `tests/playwright.config.ts`：headless chromium + webServer http-server + PLAYWRIGHT_BROWSERS_PATH
- [x] 3.3 `tests/index.html`：driver page
- [x] 3.4 `tests/host.js`：暴露 `window.__test.runR{1..7}` 实现
- [x] 3.5 `tests/r1-r7.spec.ts`：R1–R7 调用 + 断言
- [x] 3.6 `test.sh`：PATH 注入 + npm install + playwright install chromium + npx playwright test
- [x] 3.7 `.gitignore` 加 `tests/node_modules/` + `tests/test-results/` + `tests/playwright-report/`

## 阶段 4: 验证 + GREEN

- [x] 4.1 `./test.sh` 7/7 passed
- [x] 4.2 `./scripts/test-all.sh` 6 stage 全绿（wasm tests 不进默认）
- [x] 4.3 wasm node demo 仍可手动跑：`node demo/node/run.js`（fixture 路径更新后）

## 阶段 5: README + 归档

- [x] 5.1 wasm `README.md` 加 "Run tests" 段；Limitations 段移除 Tests 推迟
- [x] 5.2 `docs/design/runtime/embedding.md` §11.7 更新 wasm 行：BundleNode/BrowserResolver 读 index.json
- [x] 5.3 移 `changes/add-wasm-tests/` → `archive/2026-05-12-add-wasm-tests/`
- [x] 5.4 commit + push（type=test, scope=host/wasm）

## 备注

- chromium-only 起步（D1）；WebKit / Firefox 进 Deferred
- host.js + spec 拆分（D2）；不写 `page.evaluate(() => 完整 R1 逻辑)`
- playwright `webServer` 自启 http-server（D3）；test.sh 不手动管 server
- fixture 集中到 `js/fixtures/`（D4）；demo + tests 共享
- resolver namespace mapping 收进本 spec（D5），不另起 fix spec
- install-node-local.sh 绑本 commit（D6）
- 实施期 stale path 是 grandfather bug（build.sh 一直没更新到 redesign-artifact-layout 路径），顺手在本 spec 同 commit 修复
