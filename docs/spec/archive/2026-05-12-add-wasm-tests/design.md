# Design: wasm facade playwright tests + resolver namespace fix

## Architecture

```
host (macOS):
  ┌───────────────────────────────────────────────────────────────┐
  │ ./scripts/install-node-local.sh                               │
  │   → artifacts/tools/node/{bin/node, bin/npm, ...}             │
  └───────────────────────────────────────────────────────────────┘
                          │
                          ▼
  ┌───────────────────────────────────────────────────────────────┐
  │ platforms/wasm/build.sh                                       │
  │   1. dotnet z42c examples/embedding/hello.z42 → js/fixtures/  │
  │   2. dotnet z42c examples/embedding/multi_line.z42            │
  │   3. cp artifacts/build/libs/release/{*.zpkg,index.json}      │
  │        → js/stdlib/                                           │
  │   4. wasm-pack build --target web --out-dir pkg-web           │
  │   5. wasm-pack build --target nodejs --out-dir pkg-nodejs     │
  └───────────────────────────────────────────────────────────────┘
                          │
                          ▼
  ┌───────────────────────────────────────────────────────────────┐
  │ platforms/wasm/test.sh                                        │
  │   PATH="artifacts/tools/node/bin:$PATH"                       │
  │   PLAYWRIGHT_BROWSERS_PATH="artifacts/tools/playwright-..."   │
  │   cd tests/                                                   │
  │   npm install --prefer-offline      (one-shot via npm ci)     │
  │   npx playwright install chromium   (one-shot to local cache) │
  │   npx playwright test                                         │
  └───────────────────────────────────────────────────────────────┘
                          │
                          ▼
  ┌───────────────────────────────────────────────────────────────┐
  │ playwright auto-starts http-server on http://localhost:4242/ │
  │   serves wasm/ root                                           │
  │                                                               │
  │   headless chromium loads http://localhost:4242/tests/        │
  │     index.html imports host.js                                │
  │     host.js imports pkg-web/z42_wasm.js + stdlib-resolver.js  │
  │     host.js exposes window.__test = { runR1, runR2, ... }     │
  │                                                               │
  │   r1-r7.spec.ts calls page.evaluate(() => __test.runR1())     │
  └───────────────────────────────────────────────────────────────┘
```

## Decisions

### D1: Chromium-only first; WebKit / Firefox deferred

**问题：** playwright 可以同时跑 chromium / WebKit / Firefox。装哪几个？

**选项：**
- A. Chromium only — 1 个浏览器，~280MB
- B. Chromium + WebKit — 2 个，~600MB；WebKit 覆盖 Safari/JSC 互操作
- C. 全 3 个 — ~900MB

**决定：** A。理由：
- 主要测试目标是 wasm-bindgen 在 V8/Liftoff 下的互操作正确性 —— chromium 是最严苛 + 最常见的部署目标
- Safari / Firefox 的 wasm-bindgen 互操作历史上跟 chromium 高度兼容；marginal value 低
- 体积考虑：chromium 已经 ~280MB，再加一个翻倍；先看 chromium 跑通再扩

WebKit 单独跑（覆盖 iOS Safari 嵌入 wasm 场景）进 Deferred；触发条件：出现真实 Safari 兼容性问题。

### D2: host.js 暴露 `window.__test = { runR1, ..., runR7 }`，spec 调用 `page.evaluate`

**问题：** R1–R7 的实现写在 JS 哪里？

**选项：**
- A. **每个 `page.evaluate(() => { ... 完整 R1 实现 ... })`** —— 测试逻辑全在 spec.ts 内
- B. **host.js 暴露 `window.__test.runR1()` 等异步函数 + spec.ts 调用 `page.evaluate(() => __test.runR1())`** —— 测试逻辑在 host page，spec 只是触发 + 断言

**决定：** **B**。理由：
- `page.evaluate` 闭包内不能 `import`，所有 `import { Z42VM }` 必须在 host page 中先做好
- host.js 中的 helpers (`makeVMWithSink`, `fixture`) 一处定义 + 多 scenario 复用，spec.ts 干净
- 与 iOS XCTest 的 helpers 分布对称（Z42VMTests.swift 里有 `Collector` + `fixture` helpers）

### D3: 起 http server — playwright `webServer` 还是自己起？

**问题：** 静态文件需要 HTTP 服务（`file://` 协议不能加载 wasm + 跨文件 fetch）。

**选项：**
- A. playwright config 里写 `webServer: { command: "npx http-server ...", url: "http://localhost:4242/" }`，playwright 启停
- B. test.sh 手动 `npx http-server` 后台启 + trap EXIT 杀掉

**决定：** **A**。playwright 标准做法，自带生命周期管理；test.sh 极简化。

### D4: Test fixtures 放 `js/fixtures/` 与 demo 共享

**问题：** test fixture 路径放哪？

**选项：**
- A. `tests/fixtures/` 与 spec 同目录
- B. `js/fixtures/` 与 demo 共享（demo 现状是 `demo/fixtures/hello.zbc`，跟 demo 代码混在一起；与 npm package layout 不太一致）
- C. `js/fixtures/` 集中放，demo 引用同路径

**决定：** **C**。`demo/fixtures/` 移到 `js/fixtures/`，demo/{node,web}/run.js 改路径；tests 也读 `js/fixtures/`；build.sh 一次编译，三处共用。npm package 角度看：`js/` 是 package contents，`demo/` 是开发期示例不进 publish 的 files，fixture 跟着 js/ 走 cleaner。

### D5: 修 wasm resolver namespace mapping 收进本 spec（不另起 fix spec）

**问题：** wasm 的 namespace mapping bug 与 iOS / Android 同形，是否再起 `fix-wasm-resolver` spec？

**决定：** **不另起**，收进本 spec。理由：
- iOS / Android 的 fix-bundle-resolver-namespace-index 显式 deferred 了 wasm —— 那个 spec 的 Deferred 段就是"等 wasm spec 收回"
- wasm tests 跑不通的根因就是这个 bug，**实施 wasm tests = 顺手修 wasm resolver**；分两个 spec 会让 wasm spec 自己引用 wasm resolver 修复但又解释不通
- Scope 上 wasm/js/stdlib-resolver.js 在本 spec Scope 内

### D6: Node 装 `artifacts/tools/node/` —— install script 独立提交还是绑本 spec？

**问题：** `scripts/install-node-local.sh` 在本 spec 草稿期已写好并装了 Node。

**决定：** 绑本 spec commit（installer 本身没有独立用户；本 spec 是第一个 + 当前唯一消费者）。将来 Android tests 若也需要 npm-based 工具（KSP / detekt 等），可复用同 installer。

## Implementation Notes

### stdlib-resolver.js 改造

```js
const STDLIB_NAMES = ['z42.core', 'z42.io', 'z42.collections', 'z42.math', 'z42.test', 'z42.text'];

async function fetchIndexBrowser(baseUrl) {
    try {
        const res = await fetch(new URL('index.json', baseUrl));
        if (!res.ok) return {};
        return await res.json();
    } catch { return {}; }
}

async function readIndexNode(stdlibDir) {
    const fs = await import('node:fs');
    const path = await import('node:path');
    const file = path.join(stdlibDir, 'index.json');
    if (!fs.existsSync(file)) return {};
    try {
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch { return {}; }
}

export function mapResolver(byNamespace) {
    return function resolve(namespace) {
        return byNamespace.get(namespace) ?? null;
    };
}

export async function bundleStdlibNode() {
    const fs = await import('node:fs');
    const path = await import('node:path');
    const url = await import('node:url');
    const here = path.dirname(url.fileURLToPath(import.meta.url));
    const stdlibDir = path.join(here, 'stdlib');

    // 1. Load index. Empty {} when missing — fall back to filename below.
    const index = await readIndexNode(stdlibDir);

    // 2. Load every distinct zpkg referenced by index (de-duped — z42.core
    //    appears under multiple keys).
    const byFilename = new Map();
    for (const filename of new Set(Object.values(index))) {
        const file = path.join(stdlibDir, filename);
        if (fs.existsSync(file)) {
            byFilename.set(filename, new Uint8Array(fs.readFileSync(file)));
        }
    }
    // 3. Also load each known STDLIB_NAME as `<name>.zpkg` for backward-compat.
    for (const name of STDLIB_NAMES) {
        const file = path.join(stdlibDir, `${name}.zpkg`);
        if (fs.existsSync(file) && !byFilename.has(`${name}.zpkg`)) {
            byFilename.set(`${name}.zpkg`, new Uint8Array(fs.readFileSync(file)));
        }
    }

    // 4. Build the namespace → bytes map.
    const byNamespace = new Map();
    for (const [namespace, filename] of Object.entries(index)) {
        const bytes = byFilename.get(filename);
        if (bytes) byNamespace.set(namespace, bytes);
    }
    // 5. Fallback: each STDLIB_NAME maps to its same-name file (namespace ==
    //    filename convention for hosts not shipping an index).
    for (const name of STDLIB_NAMES) {
        if (!byNamespace.has(name)) {
            const bytes = byFilename.get(`${name}.zpkg`);
            if (bytes) byNamespace.set(name, bytes);
        }
    }

    return mapResolver(byNamespace);
}
```

`bundleStdlibBrowser` 镜像 `bundleStdlibNode` 但用 `fetch`。

### tests/playwright.config.ts

```ts
import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
    testDir: '.',
    fullyParallel: false,
    workers: 1,
    use: { baseURL: 'http://localhost:4242', },
    projects: [
        { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    ],
    webServer: {
        command: 'npx http-server ../ -p 4242 -c-1 --cors',
        port: 4242,
        reuseExistingServer: !process.env.CI,
        timeout: 30_000,
    },
});
```

### tests/index.html + host.js

```html
<!doctype html>
<html><head><meta charset="utf-8"><title>z42 wasm tests</title></head>
<body>
<script type="module">
  import init, { Z42VM } from '../pkg-web/z42_wasm.js';
  import { mapResolver, bundleStdlibBrowser } from '../js/stdlib-resolver.js';

  await init();

  const FIXTURE = (name) => new URL(`../js/fixtures/${name}.zbc`, location.origin + '/').href;
  async function fetchBytes(url) {
      return new Uint8Array(await (await fetch(url)).arrayBuffer());
  }

  window.__test = {
      async runR1() { /* smoke: hello.zbc → "hello, world\n" */ },
      async runR2() { /* bad zbc → status 10 */ },
      async runR3() { /* App.Ghost → status 20 */ },
      async runR4() { /* invoke wrong arg → status 21 */ },
      async runR5() { /* MapResolver only Std.Phantom → 10/30 */ },
      async runR6() { /* lifecycle × 3 */ },
      async runR7() { /* multi_line → "a\nb\nc\n" */ },
  };
  window.__ready = true;
</script>
</body></html>
```

### tests/r1-r7.spec.ts

```ts
import { test, expect } from '@playwright/test';

test.beforeEach(async ({ page }) => {
    await page.goto('/tests/');
    await page.waitForFunction(() => (window as any).__ready === true);
});

test('R1 smoke / hello world', async ({ page }) => {
    const out = await page.evaluate(() => (window as any).__test.runR1());
    expect(out).toBe('hello, world\n');
});
// ... R2 ... R7
```

### test.sh

```bash
NODE_BIN="$ROOT/artifacts/tools/node/bin"
export PATH="$NODE_BIN:$PATH"
export PLAYWRIGHT_BROWSERS_PATH="$ROOT/artifacts/tools/playwright-browsers"

cd "$HERE/tests"
[[ -d node_modules ]] || npm install
npx playwright install chromium    # idempotent
npx playwright test
```

## Risk

- **wasm-pack 在不同 Node 版本下行为变化**：v22 LTS 应该稳定，但首次跑可能出现兼容性问题；spec 落地时实测确认
- **playwright chromium 下载时间**：首次 ~30s；后续 cached
- **stdlib namespace 映射如果 v0.1 与未来 stdlib reorganize 不一致**：index.json 已经把这个责任移到 build-stdlib.sh，wasm resolver 跟着 index 走，drift 风险与 iOS/Android 相同（低，因为 index 是单一真相）

## Deferred / Future Work

### wasm-tests-webkit-firefox: 跨浏览器测试

- **来源**：本 spec 草稿期 D1
- **触发原因**：起步只装 chromium 控制体积
- **前置依赖**：无
- **触发条件**：出现真实 Safari / Firefox 上的 wasm-bindgen 兼容性问题
- **当前 workaround**：playwright config 单 chromium project；扩展只需追加 `projects: [...]` 条目

### wasm-tests-in-test-all: 接进 `./scripts/test-all.sh` 默认 GREEN

- **来源**：本 spec 草稿期 Open Q3
- **触发原因**：playwright 测试要装本地 Node + 浏览器，比 6 stage 现有依赖（cargo / dotnet）多；不进默认避免 GREEN bar 抬高
- **触发条件**：CI 标准化 / 多机器跑通后
- **当前 workaround**：手动 `./test.sh`；test-all.sh `--with-wasm` flag 是 v2 候选
