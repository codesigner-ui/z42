# toolchain/host — z42 宿主集成

## 职责

将 z42 VM 嵌入外部宿主环境的集成层。提供稳定的 C ABI / Rust API，供 IDE、GUI 应用、移动端 app（iOS / Android / wasm）或其他运行时加载和调用 z42 模块。

不做：VM 执行引擎本身（归 `runtime/`）、调试协议（归 `debugger/`）、Tier 3 各平台 facade（归 `platform/{ios,android,wasm}/`）。

## 模块状态

详细规范见 [docs/design/runtime/embedding.md](../../../docs/design/runtime/embedding.md)。

| 模块 | 职责 | 状态 |
|------|------|------|
| Tier 1 C ABI（[`src/runtime/include/z42_host.h`](../../runtime/include/z42_host.h) + [`src/runtime/src/host/`](../../runtime/src/host/)）| 稳定 C 接口：initialize / load_zbc / resolve_entry / invoke / sinks / shutdown | 🟢 完整 lifecycle + 真正的 load/invoke |
| [`embed/`](embed/) | Tier 2 Rust crate `z42-host`（`Host::new()` 安全封装；`Drop` 自动 shutdown）| 🟢 H2b |
| [`examples/hello_rust/`](examples/hello_rust/) | 桌面 Rust 示例 —— 跑通 hello-world，stdout sink 收到输出 | 🟢 H2b |
| [`examples/hello_c/`](examples/hello_c/) | 桌面 C 参考源码 —— 头文件正确，desktop staticlib build 留 H4 一并做 | 🔵 reference only |
| `platforms/{ios,android,wasm}/` | Tier 3 facade（归各平台 spec）| 📋 H4（P4.3 / P4.4 / P4.2）|

## 阶段进度

- ✅ **H0** 设计文档 + docs/spec/changes 四件套（`docs/spec/archive/2026-05-10-add-embedding-api/`）
- ✅ **H1** Tier 1 C ABI scaffold —— 单实例 lifecycle，12 个 unit test
- ✅ **H2-core** `load_zbc` / `resolve_entry` / `invoke` 全链路 + stdout sink 接 VM + 集成测试 hello-world
- ✅ **H2b** Tier 2 `z42-host` crate + `examples/hello_rust` 端到端跑通 + `examples/hello_c` 参考源码
- ✅ **H3** 错误路径全覆盖（17 个 host:: 测试 / 5 类错误：BadZbc / EntryNotFound / ArgMismatch / VmException / sink ordering）
- ✅ **H4-prereq** [`add-zpkg-resolver-hook`](../../../docs/spec/archive/2026-05-12-add-zpkg-resolver-hook/) — `Z42ZpkgResolverFn` C ABI + `ZpkgResolver` Rust trait + `MapResolver` / `SearchPathsResolver`（22 个 host:: 测试）
- 🟢 **H4 (wasm)** [`add-platform-wasm`](../../../docs/spec/archive/2026-05-12-add-platform-wasm/) ✅ —— `@z42/wasm` npm 包；`Z42VM` JS class；wasm-pack web + nodejs 双 target；node demo 跑通；**附带** runtime `native-interop` feature 拆分（让 wasm 能跳过 libffi/libloading）
- 📋 **H4 (ios / android)** 移动平台 facade 接入（归 `add-platform-ios` / `add-platform-android` spec）
- 📋 **H5** test-runner library 重构到 `z42-host` 之上（归 runner spec）

## Quick Start

```sh
# 1. 编译器 + stdlib（一次性）
dotnet build src/compiler/z42.slnx

# 2. 跑 cargo 集成测试（自动编译 fixture）
cargo test --manifest-path src/runtime/Cargo.toml --lib host::

# 3. 跑外部示例
dotnet artifacts/compiler/z42.Driver/bin/z42c.dll \
    src/runtime/tests/data/embedding_hello/source.z42 \
    --emit zbc -o /tmp/embedding_hello.zbc
cargo run --manifest-path src/toolchain/host/examples/hello_rust/Cargo.toml -- \
    /tmp/embedding_hello.zbc artifacts/z42/libs
# 期望：[host] Hello, World!
```

## 依赖关系

- 依赖 `runtime/`（host 模块在 `src/runtime/src/host/` 内，通过 `extern "C"` 暴露给宿主）
- Tier 2 / Tier 3 / examples 依赖 Tier 1 C ABI；不直连 VM internals
- 被外部应用消费（非 z42 编译器 / VM 内部使用）

## 与 interop 的边界

| 方向 | 解决问题 | 文档 |
|------|---------|------|
| **interop** | native 代码 → 注册类型/方法进 z42（CPython C 扩展类比）| [docs/design/language/interop.md](../../../docs/design/language/interop.md) |
| **host (本模块)** | 宿主 app → 启动 VM → 加载 .zbc → 调用入口 → 关闭（CoreCLR `coreclrhost.h` 类比）| [docs/design/runtime/embedding.md](../../../docs/design/runtime/embedding.md) |

两者复用同一份 `Z42Value` / `Z42Args` / `Z42Error` 类型定义（在 `z42_abi.h`），互不重叠。
