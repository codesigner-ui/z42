# Tasks: Add WebAssembly Platform Scaffold

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P4.1 (add-runtime-feature-flags) 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: 工程骨架
- [ ] 阶段 2: Rust wasm crate
- [ ] 阶段 3: JS 入口与类型
- [ ] 阶段 4: 构建脚本
- [ ] 阶段 5: 浏览器 + Node demo
- [ ] 阶段 6: playwright e2e
- [ ] 阶段 7: just / CI 接入
- [ ] 阶段 8: 文档同步
- [ ] 阶段 9: 验证

---

## 阶段 1: 工程骨架

- [ ] 1.1 [platform/](platform/) 顶层目录与 README
- [ ] 1.2 [platform/wasm/](platform/wasm/) 目录骨架
- [ ] 1.3 [platform/wasm/.gitignore](platform/wasm/.gitignore) 忽略 pkg-* / node_modules / playwright-report
- [ ] 1.4 [platform/wasm/README.md](platform/wasm/README.md) 工程文档
- [ ] 1.5 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[workspace] members` 加 `../platform/wasm`

## 阶段 2: Rust wasm crate

- [ ] 2.1 [platform/wasm/Cargo.toml](platform/wasm/Cargo.toml) 按 design.md Decision 4
- [ ] 2.2 [platform/wasm/src/lib.rs](platform/wasm/src/lib.rs) Z42Vm wasm-bindgen 入口
- [ ] 2.3 [platform/wasm/src/error.rs](platform/wasm/src/error.rs) WasmError → JsValue
- [ ] 2.4 验证：`cargo build --target wasm32-unknown-unknown -p z42-wasm` 通过

## 阶段 3: JS 入口与类型

- [ ] 3.1 [platform/wasm/package.json](platform/wasm/package.json) npm 元数据
- [ ] 3.2 [platform/wasm/js/index.d.ts](platform/wasm/js/index.d.ts) TypeScript 完整类型（design.md Decision 2）
- [ ] 3.3 [platform/wasm/js/index.js](platform/wasm/js/index.js) re-export wasm-pack 产物（按 target）

## 阶段 4: 构建脚本

- [ ] 4.1 [platform/wasm/build.sh](platform/wasm/build.sh) 三 target wasm-pack（design.md Decision 6）
- [ ] 4.2 chmod +x

## 阶段 5: 浏览器 + Node demo

- [ ] 5.1 [platform/wasm/demo/browser/index.html](platform/wasm/demo/browser/index.html)
- [ ] 5.2 [platform/wasm/demo/browser/index.js](platform/wasm/demo/browser/index.js)
- [ ] 5.3 [platform/wasm/demo/node/run.js](platform/wasm/demo/node/run.js)
- [ ] 5.4 demo 前置：编译 examples/01_hello.z42 → .zbc 的 helper 脚本

## 阶段 6: playwright e2e

- [ ] 6.1 [platform/wasm/test/package.json](platform/wasm/test/package.json) playwright 依赖
- [ ] 6.2 [platform/wasm/test/playwright.config.ts](platform/wasm/test/playwright.config.ts) 配置
- [ ] 6.3 [platform/wasm/test/wasm.spec.ts](platform/wasm/test/wasm.spec.ts) hello world e2e
- [ ] 6.4 至少加 5 个 vm_core 子集测试（验证 cross-platform 一致性）

## 阶段 7: just / CI 接入

- [ ] 7.1 [justfile](justfile) 替换 `platform` 占位为完整实现（design.md Decision 10）
- [ ] 7.2 加 `platform-wasm-build` / `platform-wasm-build-debug` / `platform-wasm-demo` / `platform-wasm-test` 子任务
- [ ] 7.3 [.github/workflows/ci.yml](.github/workflows/ci.yml) 加 `platform-wasm` job

## 阶段 8: 文档同步

- [ ] 8.1 [docs/design/cross-platform.md](docs/design/cross-platform.md) 加 "WebAssembly" 章节
- [ ] 8.2 [platform/wasm/README.md](platform/wasm/README.md) 完整工程文档
- [ ] 8.3 [docs/dev.md](docs/dev.md) 加 "Platform: WebAssembly" 段
- [ ] 8.4 [docs/roadmap.md](docs/roadmap.md) 进度表加 P4.2 完成

## 阶段 9: 验证

- [ ] 9.1 `cargo build --target wasm32-unknown-unknown -p z42-wasm` 通过
- [ ] 9.2 `./platform/wasm/build.sh release` 产出 3 个 pkg 目录
- [ ] 9.3 `node platform/wasm/demo/node/run.js` 输出 "Hello, World!"
- [ ] 9.4 浏览器打开 demo 显示 "Hello, World!"
- [ ] 9.5 playwright chromium 测试通过
- [ ] 9.6 5 个 vm_core 子集在 wasm 跑出与 desktop 完全一致输出
- [ ] 9.7 wasm 二进制 release 大小 ≤ 5 MB
- [ ] 9.8 CI platform-wasm job 全绿
- [ ] 9.9 TypeScript 类型在 VS Code 中正确（hover / autocomplete）

## 备注

### 实施依赖

- 必须先完成 P4.1（feature flags）
- 不依赖 P4.3 / P4.4

### 风险

- **风险 1**：z42 interp 依赖 std::println! → wasm 端无 stdio；需先简化为 console.log fallback（v0.1 限制）
- **风险 2**：z42 runtime 链接的某些 crate 不支持 wasm32（如某些 native deps） → 实施前先 `cargo build --target wasm32-unknown-unknown` 列出错误
- **风险 3**：wasm-pack 与 cargo workspace 兼容性偶尔有坑 → 退路是脱离 workspace 单独 cargo 项目
- **风险 4**：CI 上 chromium 安装慢 → 用 `playwright install chromium --with-deps`
- **风险 5**：demo 中 fetch examples/01_hello.zbc 在 file:// 协议失败 → 强制走 http server

### 工作量估计

2–3 天（含 wasm-pack 调试 + playwright 配置）。
