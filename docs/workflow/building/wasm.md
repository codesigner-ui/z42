# Platform: WASM — build & run requirements

> **状态**：🟢 已落地（2026-05-12）。详细设计见 [`docs/spec/archive/2026-05-12-add-platform-wasm/`](../../spec/archive/2026-05-12-add-platform-wasm/)。
> **facade 文档**：[`platforms/wasm/README.md`](../../../src/toolchain/host/platforms/wasm/README.md)。
> **共同前置**：[`README.md`](README.md)。

把 z42 VM 编译成 WebAssembly，让 JavaScript / TypeScript 宿主（浏览器 / Node.js / 任意 wasm runtime）一行 `import` 跑 `.zbc`。

## 工具链清单

| 工具 | 用途 | 安装 |
|------|------|------|
| `rustup` + stable rust 1.85+ | Rust 编译器 | https://rustup.rs |
| `wasm32-unknown-unknown` target | wasm 后端 | `rustup target add wasm32-unknown-unknown` |
| `wasm-pack` 0.14+ | wasm-bindgen 打包 + JS 胶水生成 | `cargo install wasm-pack --locked` |
| Node.js 18+ | 跑 node demo / npm 发布 | `brew install node` 或任意发行版 |
| `dotnet` 8.0+ | 编 z42 stdlib（共同前置）| https://dotnet.microsoft.com/download |

## 一次性环境准备

```bash
# 1. Rust target + wasm-pack
rustup target add wasm32-unknown-unknown
cargo install wasm-pack --locked

# 2. Node.js（可选；只跑 node demo 才需）
brew install node          # macOS
# 或：apt install nodejs / 等

# 3. 编译器 + stdlib（必须；产出 z42c.dll + zpkg）
dotnet build src/compiler/z42.slnx
```

`cargo install wasm-pack` 在 rustc 1.88 上要加 `--locked`，否则 cargo-platform crate 解析失败。

## 构建步骤

```bash
cd src/toolchain/host/platforms/wasm
./build.sh
```

`build.sh` 一站式执行：

1. **工具链检查**（fail-fast）—— `wasm-pack` 在 PATH + `wasm32-unknown-unknown` target 已装
2. **fixture 编译** —— `dotnet z42c.dll demo/fixtures/hello.z42 --emit zbc -o demo/fixtures/hello.zbc`（仅当 `artifacts/compiler/z42.Driver/bin/z42c.dll` 存在）
3. **stdlib 拷贝** —— `artifacts/z42/libs/*.zpkg` → `js/stdlib/`
4. **wasm-pack** —— `--target web -d pkg-web` 与 `--target nodejs -d pkg-nodejs` 各跑一次

构建后产物：

```
src/toolchain/host/platforms/wasm/
├── pkg-web/{z42_wasm.js, z42_wasm_bg.wasm, package.json}      # 浏览器 / 通用 ES module
├── pkg-nodejs/{z42_wasm.js, z42_wasm_bg.wasm, package.json}   # Node.js CJS
└── js/stdlib/{z42.core, z42.io, ...}.zpkg                     # bundled stdlib
```

所有产物 `.gitignore` 排除；CI 在 release 时上传。

## 跑 Node demo

```bash
node src/toolchain/host/platforms/wasm/demo/node/run.js
```

期望输出：

```
[host] Hello, World!
```

`[host]` 前缀来自 demo 注册的 `stdoutHandler`，证明 z42 的 `Console.WriteLine` 输出通过 wasm-bindgen 回调被 JS 接到，而不是直接写 stdout。

## API 速记

```ts
import init, { Z42VM } from '@z42/wasm';
import { bundleStdlibNode } from '@z42/wasm/stdlib-resolver';

await init();
const vm = new Z42VM({
    zpkgResolver: await bundleStdlibNode(),
    stdoutHandler: (bytes) => process.stdout.write(bytes),
});
const m  = vm.loadZbc(zbcBytes);
const e  = vm.resolveEntry(m, 'My.Namespace.Main');
vm.invoke(e);
vm.dispose();
```

完整 TS 类型 + 错误码映射见 [`platforms/wasm/README.md`](../../../src/toolchain/host/platforms/wasm/README.md)。

## 限制（v0.1）

- 仅 interp 模式（wasm 沙箱禁 JIT）
- 无文件系统：必须用 `zpkgResolver` 喂 zpkg 字节
- 同步 invoke（长任务阻塞 JS 主线程，需要用户 `Worker`）
- marshal 限 null / boolean / number / bigint；string / object / Array 推迟

## 故障排查

| 现象 | 处理 |
|------|------|
| `wasm-pack: command not found` | `cargo install wasm-pack --locked` |
| `error[E0463]: can't find crate for 'std'` (wasm32) | `rustup target add wasm32-unknown-unknown` |
| `libffi-sys` 报 `region` 编译错误 | 出现说明 wasm feature preset 拉了 `native-interop`，但本应 feature `wasm = ["interp-only"]` 不含 —— 检查 Cargo.toml 是否被改 |
| `fixture missing: hello.zbc` | 先跑 `dotnet build src/compiler/z42.slnx`，再 `./build.sh` |
| `Z42VMError: undefined function Std.IO.Console.WriteLine` | stdlib zpkg 没载入。检查 `js/stdlib/` 是否含 `z42.io.zpkg`，必要时重跑 `build.sh` |
| `numz42-c` 编译错误（含 `stdlib.h not found`） | runtime/build.rs 应该自动跳过；若没跳过，设 `Z42_SKIP_NATIVE_POC=1` 重试 |
| Node demo 报 `import init from ...` 失败 | `pkg-nodejs/` 不存在 —— 先 `./build.sh` |

## 关联文档

- 平台 facade：[`src/toolchain/host/platforms/wasm/README.md`](../../../src/toolchain/host/platforms/wasm/README.md)
- 设计与决策：[`docs/spec/archive/2026-05-12-add-platform-wasm/`](../../spec/archive/2026-05-12-add-platform-wasm/)
- 跨平台契约：[`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)
- Embedding API 整体：[`docs/design/runtime/embedding.md`](../../design/runtime/embedding.md)
