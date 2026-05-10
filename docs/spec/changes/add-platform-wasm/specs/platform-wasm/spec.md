# Spec: WebAssembly Platform Scaffold

## ADDED Requirements

### Requirement: 工程目录结构

#### Scenario: platform/wasm/ 含必要文件

- **WHEN** 检查 `platform/wasm/`
- **THEN** 含 `Cargo.toml`、`src/lib.rs`、`build.sh`、`package.json`、`README.md`、`demo/`、`test/`

#### Scenario: 顶层 platform/ README 存在

- **WHEN** 检查 `platform/README.md`
- **THEN** 文件存在；列出 wasm / ios / android 三个子目录

---

### Requirement: Rust 端 wasm crate

#### Scenario: cdylib + rlib 双 crate-type

- **WHEN** 阅读 [platform/wasm/Cargo.toml](platform/wasm/Cargo.toml)
- **THEN** `[lib] crate-type = ["cdylib", "rlib"]`

#### Scenario: 启用 z42-runtime wasm feature

- **WHEN** 阅读依赖段
- **THEN** `z42-runtime` 用 `default-features = false, features = ["wasm"]`

#### Scenario: 编译为 wasm32 target

- **WHEN** 执行 `cargo build --target wasm32-unknown-unknown -p z42-wasm`
- **THEN** 编译成功，产物为 `.wasm` 文件

---

### Requirement: JS API

#### Scenario: 类型定义完整

- **WHEN** 阅读 [platform/wasm/js/index.d.ts](platform/wasm/js/index.d.ts)
- **THEN** 含 `Z42Vm` 类与 `init` 函数完整定义；含 `loadZpkg` / `loadZbc` / `run` / `setStdoutHandler` / `setStderrHandler` / `free`

#### Scenario: ESM 入口

- **WHEN** 通过 `import init, { Z42Vm } from '@z42/wasm'` 加载
- **THEN** 解析成功（ESM target）

#### Scenario: CJS 入口

- **WHEN** 通过 `require('@z42/wasm')` 加载
- **THEN** 解析成功（CJS / Node.js target）

---

### Requirement: 构建工具

#### Scenario: build.sh 三 target

- **WHEN** 执行 `./platform/wasm/build.sh release`
- **THEN** 产出 `pkg-web/`、`pkg-node/`、`pkg-bundler/` 三个目录
- **AND** 每个目录含 `*.js`、`*.d.ts`、`*.wasm`、`package.json`

#### Scenario: debug 与 release 区分

- **WHEN** 执行 `./build.sh debug` 与 `./build.sh release`
- **THEN** debug 模式产物明显较大（含调试符号）；release 用 wasm-pack 默认优化

---

### Requirement: 浏览器 demo

#### Scenario: 浏览器加载 hello.zbc 输出

- **WHEN** 在浏览器打开 `platform/wasm/demo/browser/index.html`（本地 http server）
- **THEN** 页面加载后 `#output` 元素显示 "Hello, World!"

#### Scenario: 自定义 stdout handler 生效

- **WHEN** demo 中调用 `vm.setStdoutHandler((s) => /* 写到 DOM */)`
- **THEN** `Console.println` 输出走该 handler（v0.1 fallback 到 console.log 也接受）

---

### Requirement: Node demo

#### Scenario: Node 跑通 hello

- **WHEN** 执行 `node platform/wasm/demo/node/run.js`
- **THEN** stdout 输出 "Hello, World!"，exit 0

---

### Requirement: playwright e2e

#### Scenario: chromium 测试通过

- **WHEN** 执行 `cd platform/wasm/test && npx playwright test`
- **THEN** 至少 1 个测试通过（"Hello World runs in browser"）

#### Scenario: 测试可在 CI 跑

- **WHEN** CI 上跑 `just platform wasm test`
- **THEN** chromium headless 模式通过

---

### Requirement: just 入口

#### Scenario: just platform wasm build

- **WHEN** 执行 `just platform wasm build`
- **THEN** 触发 `./platform/wasm/build.sh release`，产出 pkg-* 目录

#### Scenario: just platform wasm test

- **WHEN** 执行 `just platform wasm test`
- **THEN** 先 build debug，再跑 playwright；exit 反映测试结果

#### Scenario: just platform wasm demo

- **WHEN** 执行 `just platform wasm demo`
- **THEN** 启动本地 http server 在 8080，可在浏览器打开 demo

#### Scenario: 未知平台报错

- **WHEN** 执行 `just platform unknown build`
- **THEN** 报错并 exit 非零

---

### Requirement: CI 接入

#### Scenario: platform-wasm job

- **WHEN** PR 触发 CI
- **THEN** 含 `platform-wasm` job
- **AND** 安装 wasm32 target、wasm-pack、Node 20
- **AND** 跑 `just platform wasm build` 与 `just platform wasm test`，全绿

---

### Requirement: 跨平台一致性 (vm_core 子集)

#### Scenario: vm_core 子集在 wasm 跑通

- **WHEN** 选取 `src/runtime/tests/vm_core/` 中 5 个不依赖 stdlib 的最小用例
- **WHEN** 在 wasm 端依次加载 .zbc 并 run
- **THEN** 输出与 desktop interp 模式完全一致（字节级对比）

---

### Requirement: 文档同步

#### Scenario: cross-platform.md 含 wasm 章节

- **WHEN** 阅读 [docs/design/cross-platform.md](docs/design/cross-platform.md)
- **THEN** 含 "WebAssembly" 章节，包括架构、JS API、限制（无 JIT / 无 IO）、构建步骤

#### Scenario: platform/wasm/README.md 完整

- **WHEN** 阅读 [platform/wasm/README.md](platform/wasm/README.md)
- **THEN** 含安装依赖、构建步骤、API 用法、demo 运行说明

#### Scenario: dev.md 含 wasm 段

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 含 "Platform: WebAssembly" 段，列出 just 命令
