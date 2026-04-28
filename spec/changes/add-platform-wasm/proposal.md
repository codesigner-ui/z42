# Proposal: Add WebAssembly Platform Scaffold

## Why

z42 当前**无 WebAssembly 支持**。让 z42 跑在浏览器 / Node.js 是吸引前端 / 全栈开发者的关键路径，也是验证 interp-only 路径正确性的最低成本方式（无需移动设备 / 模拟器）。

P4.2 是 P4 跨平台的**第一个落地平台**（最低风险，最易验证）：

- wasm 沙箱**禁动态代码生成** → 必然 interp-only，与 P4.1 的 `wasm = ["interp-only"]` 直接对应
- 工具链成熟（wasm-bindgen / wasm-pack / playwright）
- 验证收益：跑通后证明 z42 的 interp 路径是平台无关的，为 iOS / Android 铺路

## What Changes

- **新建 [platform/wasm/](platform/wasm/) 目录**：
  - Rust crate（cdylib）封装 z42-runtime
  - wasm-bindgen 暴露 JS API（`Z42Vm` 类）
  - npm package 配置（package.json）
  - 浏览器 demo HTML + JS
  - Node.js demo
  - playwright e2e 测试
- **构建脚本** `platform/wasm/build.sh`：用 `wasm-pack` 构建 web + node 双 target
- **just 接入**：`just platform wasm build` / `just platform wasm test` / `just platform wasm demo`
- **CI 接入**：linux runner 上加一个 platform-wasm job
- **文档**：[platform/wasm/README.md](platform/wasm/README.md) + [docs/design/cross-platform.md](docs/design/cross-platform.md) wasm 段

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `platform/wasm/Cargo.toml` | NEW | crate manifest，crate-type=cdylib |
| `platform/wasm/src/lib.rs` | NEW | wasm-bindgen 入口 + Z42Vm 包装 |
| `platform/wasm/src/error.rs` | NEW | WasmError 类型（JsValue 转换） |
| `platform/wasm/build.sh` | NEW | wasm-pack build --target web + --target nodejs |
| `platform/wasm/package.json` | NEW | npm package 元数据 |
| `platform/wasm/.gitignore` | NEW | 忽略 pkg/ pkg-node/ 等产物 |
| `platform/wasm/README.md` | NEW | 工程文档 |
| `platform/wasm/js/index.js` | NEW | npm 入口（re-export wasm-pack 产物） |
| `platform/wasm/js/index.d.ts` | NEW | TypeScript 类型定义 |
| `platform/wasm/demo/browser/index.html` | NEW | 浏览器 demo |
| `platform/wasm/demo/browser/index.js` | NEW | demo JS（加载 hello.zbc） |
| `platform/wasm/demo/node/run.js` | NEW | Node 端 demo 脚本 |
| `platform/wasm/test/wasm.spec.ts` | NEW | playwright e2e |
| `platform/wasm/test/playwright.config.ts` | NEW | playwright 配置 |
| `platform/wasm/test/package.json` | NEW | playwright 依赖 |
| `platform/README.md` | NEW | platform/ 顶层 README |
| `justfile` | MODIFY | 替换 `platform` 占位为完整实现（含 wasm 子任务） |
| `.github/workflows/ci.yml` | MODIFY | 加 platform-wasm job |
| `docs/design/cross-platform.md` | MODIFY | wasm 段（架构 / API / 限制） |
| `docs/dev.md` | MODIFY | 加 "Platform: WebAssembly" 段 |
| `src/runtime/Cargo.toml` | MODIFY | `[workspace] members` 加 `../platform/wasm`（若选 workspace） |

**只读引用**：
- [src/runtime/](src/runtime/) — 理解 Interpreter API
- [examples/](examples/) — demo 用 example .zbc
- [docs/design/cross-platform.md](docs/design/cross-platform.md) — P4.1 已建好

## Out of Scope

- **JIT 在 wasm 上跑**：wasm 沙箱禁，不可能
- **WASI 接入**：本 spec 只跑 wasm32-unknown-unknown（浏览器 + Node）；WASI / WASIp2 留给独立 spec
- **wasm threads / SharedArrayBuffer**：本 spec 单线程
- **IO 外部资源**（fetch、文件系统）：本 spec 只暴露内存中的 .zpkg 加载；Console.println 走 console.log
- **打包优化**（tree-shaking、代码分割）：本 spec 用 wasm-pack 默认配置
- **iOS / Android 工程**：P4.3 / P4.4 范围
- **大型示例**（一个完整的浏览器 IDE）：本 spec 只交付最小 hello demo

## Open Questions

- [ ] **Q1**：用 wasm-pack 还是手写 cargo build + wasm-bindgen-cli？
  - 倾向：wasm-pack（事实标准，自动生成 npm 包结构）
- [ ] **Q2**：JS API 风格 ESM 还是 CJS？
  - 倾向：ESM 主推；CJS 通过 wasm-pack `--target nodejs` 单独输出
- [ ] **Q3**：Console.println 在 wasm 中如何输出？
  - 倾向：暴露 JS 回调 `Z42Vm.setStdoutHandler((s) => ...)`，默认走 `console.log`
- [ ] **Q4**：playwright 测试在 CI 用 chromium / firefox / webkit?
  - 倾向：先 chromium（最快）；通过后扩展
- [ ] **Q5**：是否纳入 z42 的 cargo workspace？
  - 倾向：是（`../platform/wasm` 加入 members；统一 build / version）
