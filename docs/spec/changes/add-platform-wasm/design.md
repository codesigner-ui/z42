# Design: WebAssembly Platform Scaffold

---

## 🔄 REVISION 2026-05-11

> 本 spec 原稿（2026-04-29）写于 [embedding API](../../archive/2026-05-10-add-embedding-api/) 之前，
> 自定义 `Z42Vm` JS 类直接 wrap `z42_runtime::interp::Interpreter`。embedding API 落地后，
> WASM facade 改为**统一架在 Tier 2 `z42-host` crate 之上**。本节是新架构的**权威定义**；
> 原稿后续小节中**与本节冲突的部分以本节为准**。
>
> 跨平台共同契约：[`src/toolchain/host/platforms/README.md`](../../../../src/toolchain/host/platforms/README.md)
> 前置 ABI：[`add-zpkg-resolver-hook`](../add-zpkg-resolver-hook/) 必须先落地

### 修订后的架构

```
┌────────────────────────────────────────────────────────────┐
│         Browser / Node.js / wasm runtime                   │
│                                                            │
│  import init, { Z42VM } from '@z42/wasm';                  │
│  await init();                                             │
│  const corelibBytes = await fetch('stdlib/z42.core.zpkg')  │
│                          .then(r => r.arrayBuffer());      │
│  const vm = new Z42VM({                                    │
│    zpkgResolver: new MapZpkgResolver({                     │
│      'z42.core': new Uint8Array(corelibBytes),             │
│      'Std.IO':   new Uint8Array(await ioBytes),            │
│    }),                                                     │
│    stdoutHandler: (bytes) =>                               │
│      console.log(new TextDecoder().decode(bytes)),         │
│  });                                                       │
│  const m = vm.loadZbc(userBytes);                          │
│  const e = vm.resolveEntry(m, 'App.Main');                 │
│  vm.invoke(e);                                             │
│  vm.dispose();                                             │
└─────────────────────────┬──────────────────────────────────┘
                          │ wasm-bindgen JS ↔ Rust
                          ▼
┌────────────────────────────────────────────────────────────┐
│  platforms/wasm/  (cdylib, target wasm32-unknown-unknown)  │
│                                                            │
│  src/lib.rs    — #[wasm_bindgen] pub struct Z42VM          │
│                  → 内部 host: z42_host::Host               │
│  src/resolver.rs JS callback ↔ Rust ZpkgResolver bridge    │
│  js/           — npm package（@z42/wasm）                  │
│                  index.d.ts / index.js / stdlib/*.zpkg     │
└─────────────────────────┬──────────────────────────────────┘
                          │
                          ▼
┌────────────────────────────────────────────────────────────┐
│  src/toolchain/host/embed/  (Tier 2 z42-host crate)        │
└─────────────────────────┬──────────────────────────────────┘
                          ▼
┌────────────────────────────────────────────────────────────┐
│  src/runtime/  (interp-only feature; jit/aot 关闭)         │
└────────────────────────────────────────────────────────────┘
```

**目录归属变更**：`platform/wasm/` → **`src/toolchain/host/platforms/wasm/`**。原稿 §Scope 表中所有 `platform/wasm/` 路径替换为 `src/toolchain/host/platforms/wasm/`。

### 修订后的 TypeScript Facade API（权威）

```typescript
// platforms/wasm/js/index.d.ts
export default function init(input?: BufferSource | URL): Promise<void>;

export class Z42VM {
    constructor(options?: Z42VMOptions);

    stdoutHandler: ((bytes: Uint8Array) => void) | null;
    stderrHandler: ((bytes: Uint8Array) => void) | null;

    loadZbc(bytes: Uint8Array): Z42VMModule;
    resolveEntry(module: Z42VMModule, fqn: string): Z42VMEntry;
    invoke(entry: Z42VMEntry, args?: Z42VMValue[]): Z42VMValue;

    dispose(): void;   // → z42_host_shutdown
}

export interface Z42VMOptions {
    zpkgResolver?: ZpkgResolver;
    stdoutHandler?: (bytes: Uint8Array) => void;
    stderrHandler?: (bytes: Uint8Array) => void;
}

export class Z42VMModule { /* opaque */ }
export class Z42VMEntry  { /* opaque */ }

export type Z42VMValue =
    | { tag: 'null' }
    | { tag: 'i64',  v: bigint }
    | { tag: 'f64',  v: number }
    | { tag: 'bool', v: boolean };

export class Z42VMError extends Error {
    readonly status: number;   // 1..99 from Z42HostStatus
    readonly name:   string;   // "AlreadyInit" / "BadZbc" / ...
}

// ZpkgResolver = 同步函数 OR 实现接口的对象
export type ZpkgResolver =
    | ((namespace: string) => Uint8Array | null)
    | { resolve(namespace: string): Uint8Array | null };

export class MapZpkgResolver implements ZpkgResolver {
    constructor(initial?: Record<string, Uint8Array>);
    set(namespace: string, bytes: Uint8Array): void;
    resolve(namespace: string): Uint8Array | null;
}
```

### Rust 端（`src/lib.rs`）

```rust
use wasm_bindgen::prelude::*;
use z42_host::{Host, HostConfig, ZpkgResolver};

#[wasm_bindgen]
pub struct Z42VM {
    inner: Host,
}

#[wasm_bindgen]
impl Z42VM {
    #[wasm_bindgen(constructor)]
    pub fn new(options: JsValue) -> Result<Z42VM, JsValue> {
        let opts = parse_options(&options)?;
        let cfg = HostConfig {
            exec_mode: ExecMode::Interp,
            stdout: opts.stdout_sink_box(),
            stderr: opts.stderr_sink_box(),
            zpkg_resolver: Some(opts.into_zpkg_resolver()),
            search_paths: vec![],   // wasm 无 fs
            ..Default::default()
        };
        let host = Host::new(cfg).map_err(to_js_error)?;
        Ok(Z42VM { inner: host })
    }

    #[wasm_bindgen(js_name = loadZbc)]
    pub fn load_zbc(&self, bytes: &[u8]) -> Result<Z42VMModule, JsValue> { ... }

    #[wasm_bindgen(js_name = resolveEntry)]
    pub fn resolve_entry(&self, m: &Z42VMModule, fqn: &str) -> Result<Z42VMEntry, JsValue> { ... }

    pub fn invoke(&self, e: &Z42VMEntry, args: JsValue) -> Result<JsValue, JsValue> { ... }

    pub fn dispose(self) { /* Drop → z42_host_shutdown */ }
}
```

### JS hook → Rust ZpkgResolver bridge

```rust
// src/resolver.rs
struct JsCallbackResolver {
    callback: js_sys::Function,
}

unsafe impl Send for JsCallbackResolver {}
unsafe impl Sync for JsCallbackResolver {}

impl ZpkgResolver for JsCallbackResolver {
    fn resolve(&self, namespace: &str) -> Option<Vec<u8>> {
        let arg = JsValue::from_str(namespace);
        let result = self.callback.call1(&JsValue::NULL, &arg).ok()?;
        if result.is_null() || result.is_undefined() { return None; }
        // Uint8Array → Vec<u8>
        let arr = js_sys::Uint8Array::from(result);
        Some(arr.to_vec())
    }
}
```

WASM 是单线程（main thread / worker），`Send + Sync` 的 unsafe impl 在单线程环境下安全。如果将来支持 web worker pool，再细化。

### 修订后的 Rust crate

```toml
# platforms/wasm/Cargo.toml
[package]
name = "z42-platform-wasm"
edition = "2021"

[lib]
crate-type = ["cdylib"]

[dependencies]
z42_vm     = { path = "../../../runtime", default-features = false, features = ["wasm"] }
z42-host   = { path = "../embed" }
wasm-bindgen = "0.2"
js-sys       = "0.3"

[features]
default = []
```

`features = ["wasm"]` 等价于 `["interp-only"]`（[cross-platform.md](../../../design/runtime/cross-platform.md) §Features）—— JIT 自动禁用，AOT 也禁。

### 修订后的 stdlib bundle

`js/stdlib/*.zpkg` 由 build.sh 从 `artifacts/z42/libs/` 复制；npm tarball 包含这些文件。浏览器端 `fetch('@z42/wasm/stdlib/z42.core.zpkg')`，Node 端 `fs.readFileSync(require.resolve('@z42/wasm/stdlib/z42.core.zpkg'))`。

JS 端 `init()` 不再自动加载 stdlib —— 调用方显式 fetch 后传 `MapZpkgResolver` 是 v0.1 模式（同步 resolver 假设字节已在内存）。**异步 / 懒 fetch 留 0.8.x async/await spec**。

### 原稿中**仍然有效**的决策（未被本节 supersede）

- Decision 1（wasm-pack 工具链）✅
- Decision 4（Cargo.toml 配置思路）—— deps 列表替换为 z42-host
- Decision 6（build.sh 接口 / 多 target）—— 步骤不变
- Decision 7（浏览器 demo）—— `Z42Vm` → `Z42VM`，加载 stdlib bytes 后传 resolver
- Decision 8（Node demo）—— 同上
- Decision 10（justfile 接入）—— 命令不变
- Decision 11（CI）—— playwright 测试不在本次范围

### 原稿中**被 supersede 的决策**

- Decision 2 整段 JS API（`loadZpkg` / `loadZbc` 直接喂、`run(entryPoint, args)` 同步） → 改为 `loadZbc / resolveEntry / invoke` 三步 + ZpkgResolver
- Decision 3 Rust wasm-bindgen 入口（wrap `z42_runtime::interp::Interpreter`）→ wrap `z42_host::Host`
- Decision 5（stdout/stderr 桥接通过 wasm-bindgen 闭包 + thread_local）→ 改走 `z42-host` 的 sink + active flag 机制（不再 wasm-specific）

### Open Questions（修订）

- [ ] **R1**：`Z42VMValue` 在 TS 端用 discriminated union（如本文）vs `bigint` 直传？倾向 union，对 null/bool 友好
- [ ] **R2**：JS `BigInt` 处理 i64 跨平台是否引入 ergonomics 损失？倾向**接受**，i32 用 Number 已可（但 ABI 走 i64）
- [ ] **R3**：`dispose()` 后再调任何方法 → throw `Z42VMError` 而非静默？倾向**throw**，开发期更早发现 leak
- [ ] **R4**：是否同步暴露 `MapZpkgResolver` 还是只暴露 ZpkgResolver type alias？倾向**导出 class**，开发者更直观

---

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│                     Browser / Node.js                          │
│                                                                │
│  import init, { Z42Vm } from '@z42/wasm'                       │
│  await init()                                                  │
│  const vm = new Z42Vm()                                        │
│  vm.setStdoutHandler((s) => console.log(s))                    │
│  vm.loadZpkg(bytes)                                            │
│  vm.run('main')                                                │
└────────────────────────┬───────────────────────────────────────┘
                         │ wasm-bindgen JS bindings
                         ▼
┌────────────────────────────────────────────────────────────────┐
│           platform/wasm/  (cdylib, target wasm32)              │
│                                                                │
│  src/lib.rs   #[wasm_bindgen] pub struct Z42Vm                  │
│               wraps z42_runtime::interp::Interpreter           │
│                                                                │
│  src/error.rs WasmError → JsValue                              │
└────────────────────────┬───────────────────────────────────────┘
                         │ depends on
                         ▼
┌────────────────────────────────────────────────────────────────┐
│      src/runtime/  (with --features wasm = interp-only)        │
│                  从 P4.1 已支持 wasm feature                    │
└────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 工具链选型 ——「wasm-pack + wasm-bindgen」

**问题**：wasm 工具链选哪一套？

**选项**：
- A. wasm-pack（事实标准，自动 npm 包结构）
- B. cargo + wasm-bindgen-cli 手工组合
- C. trunk（适合 yew 等全栈框架）

**决定**：选 **A**。理由：
- npm 包发布零配置
- 生成 `.d.ts` TypeScript 类型
- 同时支持 web / nodejs / bundler 多 target

### Decision 2: JS API 设计（锁定）

```typescript
// platform/wasm/js/index.d.ts
export class Z42Vm {
    constructor();

    // 加载 .zpkg 二进制数据（Uint8Array）
    loadZpkg(data: Uint8Array): void;

    // 加载 .zbc 二进制数据
    loadZbc(data: Uint8Array): void;

    // 设置 stdout 输出回调（默认 console.log）
    setStdoutHandler(handler: (s: string) => void): void;

    // 设置 stderr 输出回调（默认 console.error）
    setStderrHandler(handler: (s: string) => void): void;

    // 调用入口函数；可选传字符串参数（暂不支持其他类型）
    run(entryPoint: string, args?: string[]): void;

    // 释放资源
    free(): void;
}

export interface InitOptions {
    // 可选 wasm 文件路径（默认从同目录加载）
    wasmUrl?: string;
}

export default function init(options?: InitOptions): Promise<void>;
```

### Decision 3: Rust 端 wasm-bindgen 入口（锁定）

[platform/wasm/src/lib.rs](platform/wasm/src/lib.rs)：

```rust
use wasm_bindgen::prelude::*;
use z42_runtime::interp::Interpreter;
use z42_runtime::loader::load_zpkg;

#[wasm_bindgen]
pub struct Z42Vm {
    interp: Interpreter,
    stdout_handler: Option<js_sys::Function>,
    stderr_handler: Option<js_sys::Function>,
}

#[wasm_bindgen]
impl Z42Vm {
    #[wasm_bindgen(constructor)]
    pub fn new() -> Self {
        Self {
            interp: Interpreter::new(),
            stdout_handler: None,
            stderr_handler: None,
        }
    }

    #[wasm_bindgen(js_name = loadZpkg)]
    pub fn load_zpkg(&mut self, data: &[u8]) -> Result<(), JsValue> {
        load_zpkg(&mut self.interp, data).map_err(WasmError::from)?;
        Ok(())
    }

    #[wasm_bindgen(js_name = loadZbc)]
    pub fn load_zbc(&mut self, data: &[u8]) -> Result<(), JsValue> { /* ... */ }

    #[wasm_bindgen(js_name = setStdoutHandler)]
    pub fn set_stdout_handler(&mut self, handler: js_sys::Function) {
        self.stdout_handler = Some(handler);
    }

    #[wasm_bindgen(js_name = setStderrHandler)]
    pub fn set_stderr_handler(&mut self, handler: js_sys::Function) {
        self.stderr_handler = Some(handler);
    }

    pub fn run(&mut self, entry_point: &str, args: Option<Vec<String>>) -> Result<(), JsValue> {
        // 注入 stdout/stderr handlers 到 interp 的 IO 系统
        // 调用 interp.call_function(entry_point, args.unwrap_or_default())
        Ok(())
    }
}
```

[platform/wasm/src/error.rs](platform/wasm/src/error.rs)：

```rust
use wasm_bindgen::JsValue;

pub struct WasmError(pub String);

impl From<anyhow::Error> for WasmError {
    fn from(e: anyhow::Error) -> Self {
        WasmError(format!("{:?}", e))
    }
}

impl From<WasmError> for JsValue {
    fn from(e: WasmError) -> Self {
        JsValue::from_str(&e.0)
    }
}
```

### Decision 4: Cargo.toml 配置

[platform/wasm/Cargo.toml](platform/wasm/Cargo.toml)：

```toml
[package]
name = "z42-wasm"
version = "0.1.0"
edition = "2021"

[lib]
crate-type = ["cdylib", "rlib"]

[dependencies]
z42-runtime = { path = "../../src/runtime", default-features = false, features = ["wasm"] }
wasm-bindgen = "0.2"
js-sys = "0.3"
console_error_panic_hook = { version = "0.1", optional = true }

[features]
default = ["console_error_panic_hook"]
```

`crate-type = ["cdylib", "rlib"]` 是 wasm-pack 推荐配置。

### Decision 5: stdout/stderr 桥接策略

interp 内部需要支持"自定义 IO sink"。如果当前 interp 的 print 是硬编码 `println!`，则本 spec 必须先在 [src/runtime/](src/runtime/) 加抽象（小改动），不属于平台脚手架本身但是必要前置。

**简化方案**（如果 interp 改动太大）：

- 在 wasm 端用 `web_sys::console::log_1` 直接覆盖
- 通过 panic_hook 转发输出到 console
- 接受"暂时只能默认 console.log，不能自定义 handler"作为 v0.1 限制
- 将"自定义 handler"留给后续单独 spec

**决定**：v0.1 走简化方案；setStdoutHandler API **保留**但实现内部 fallback 到 console.log。文档明确标注 "[v0.2 完整支持]"。

### Decision 6: build.sh 接口

[platform/wasm/build.sh](platform/wasm/build.sh)：

```bash
#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

mode=${1:-release}
profile_flag=""
[[ "$mode" == "release" ]] && profile_flag="--release"

# Build for web (ESM)
wasm-pack build $profile_flag --target web --out-dir pkg-web

# Build for Node.js (CJS)
wasm-pack build $profile_flag --target nodejs --out-dir pkg-node

# Build for bundlers (webpack/vite)
wasm-pack build $profile_flag --target bundler --out-dir pkg-bundler

echo "✅ wasm-pack built: pkg-web/, pkg-node/, pkg-bundler/"
```

### Decision 7: 浏览器 demo

[platform/wasm/demo/browser/index.html](platform/wasm/demo/browser/index.html)：

```html
<!DOCTYPE html>
<html>
<head><title>z42 wasm demo</title></head>
<body>
    <h1>z42 in browser</h1>
    <div id="output"></div>
    <script type="module">
        import init, { Z42Vm } from '../../pkg-web/z42_wasm.js';
        await init();

        const vm = new Z42Vm();
        const output = document.getElementById('output');
        vm.setStdoutHandler((s) => {
            output.textContent += s + '\n';
        });

        // 加载 examples/01_hello.zbc
        const resp = await fetch('../examples/01_hello.zbc');
        const bytes = new Uint8Array(await resp.arrayBuffer());
        vm.loadZbc(bytes);
        vm.run('main');
    </script>
</body>
</html>
```

### Decision 8: Node demo

[platform/wasm/demo/node/run.js](platform/wasm/demo/node/run.js)：

```javascript
const fs = require('node:fs');
const path = require('node:path');
const { Z42Vm } = require('../../pkg-node/z42_wasm.js');

const vm = new Z42Vm();
vm.setStdoutHandler((s) => process.stdout.write(s + '\n'));

const bytes = fs.readFileSync(path.join(__dirname, '../../examples/01_hello.zbc'));
vm.loadZbc(new Uint8Array(bytes));
vm.run('main');
```

### Decision 9: playwright e2e 测试

[platform/wasm/test/wasm.spec.ts](platform/wasm/test/wasm.spec.ts)：

```typescript
import { test, expect } from '@playwright/test';

test('Hello World runs in browser', async ({ page }) => {
    await page.goto('http://localhost:8080/demo/browser/');
    await expect(page.locator('#output')).toContainText('Hello, World!', { timeout: 5000 });
});
```

[platform/wasm/test/playwright.config.ts](platform/wasm/test/playwright.config.ts)：

```typescript
import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: '.',
    timeout: 30000,
    use: { headless: true },
    webServer: {
        command: 'npx http-server ../ -p 8080',
        port: 8080,
    },
    projects: [
        { name: 'chromium', use: { browserName: 'chromium' } },
    ],
});
```

### Decision 10: justfile 接入

[justfile](justfile) 替换 `platform` 占位：

```just
platform name action *args:
    #!/usr/bin/env bash
    case "{{name}}" in
        wasm) just platform-wasm-{{action}} {{args}} ;;
        ios|android) echo "P4.{{name}} 待实施" && exit 1 ;;
        *) echo "未知平台: {{name}}" && exit 1 ;;
    esac

platform-wasm-build:
    ./platform/wasm/build.sh release

platform-wasm-build-debug:
    ./platform/wasm/build.sh debug

platform-wasm-demo:
    cd platform/wasm/demo/browser && python3 -m http.server 8080

platform-wasm-test:
    just platform-wasm-build-debug
    cd platform/wasm/test && npm install && npx playwright install chromium && npx playwright test
```

### Decision 11: CI 接入

[.github/workflows/ci.yml](.github/workflows/ci.yml) 加 job：

```yaml
platform-wasm:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - uses: dtolnay/rust-toolchain@stable
      with:
        targets: wasm32-unknown-unknown
    - name: Install wasm-pack
      run: curl https://rustwasm.github.io/wasm-pack/installer/init.sh -sSf | sh
    - uses: actions/setup-node@v4
      with: { node-version: '20' }
    - name: Build wasm
      run: just platform wasm build
    - name: Test wasm e2e
      run: just platform wasm test
```

## Implementation Notes

### z42 runtime 的小改造（前置）

`run()` 调用需要 interp 接受外部 IO sink。建议：

- v0.1：interp 内部继续 println!；wasm 端用 panic_hook 转发（最简）
- v0.2：interp 加 `IoSink` trait（可被 wasm 端实现），单独 spec

### npm 发布

本 spec **不发布到 npm**（权限 / 命名空间未定）；只产出 `pkg-*/` 目录可本地 `npm link` 使用。发布留给后续运营任务。

### 对 z42 examples 的依赖

demo 引用 `examples/01_hello.zbc`，需要先编译。可以：
- 在 `platform-wasm-demo` 之前自动跑 `dotnet run --project src/compiler/z42.Driver compile examples/01_hello.z42`
- 或文档说明前置步骤

倾向：自动编译（开发者体验更好）。

### console_error_panic_hook

dev 模式启用，把 Rust panic 转成 console.error，便于调试。release 模式可关闭以减小体积。

## Testing Strategy

- ✅ `cargo build --target wasm32-unknown-unknown -p z42-wasm` 通过
- ✅ `./platform/wasm/build.sh release` 产出 pkg-web / pkg-node / pkg-bundler
- ✅ pkg-web/z42_wasm.js 有效（手动浏览器加载无错）
- ✅ pkg-node 端 `node demo/node/run.js` 输出 "Hello, World!"
- ✅ playwright chromium 测试通过
- ✅ wasm 二进制大小 release 模式 ≤ 5 MB（提示 z42 runtime 体积合理）
- ✅ CI platform-wasm job 全绿
- ✅ TypeScript 类型在 IDE 中正确
- ✅ z42 vm_core 的 5 个示例 .zbc 在 wasm 端跑通（输出与 desktop interp 一致）
