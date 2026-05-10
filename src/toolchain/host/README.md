# toolchain/host — z42 宿主集成

## 职责

将 z42 VM 嵌入外部宿主环境的集成层。提供稳定的 C ABI / Rust API，供 IDE、GUI 应用、移动端 app（iOS / Android / wasm）或其他运行时加载和调用 z42 模块。

不做：VM 执行引擎本身（归 `runtime/`）、调试协议（归 `debugger/`）、Tier 3 各平台 facade（归 `platform/{ios,android,wasm}/`）。

## 模块状态

详细规范见 [docs/design/embedding.md](../../../docs/design/embedding.md)。

| 模块 | 职责 | 状态 |
|------|------|------|
| Tier 1 C ABI（`src/runtime/include/z42_host.h` + `src/runtime/src/host/`）| 稳定 C 接口：initialize / load_zbc / resolve_entry / invoke / sinks / shutdown | 🟡 H1（lifecycle + 占位）|
| `embed/` | Tier 2 Rust crate `z42-host`（`Host::new()` 安全封装）| 📋 H2 |
| `examples/` | 最小宿主示例（C / Rust）| 📋 H2 |
| `platforms/{ios,android,wasm}/` | Tier 3 facade（归各平台 spec）| 📋 H4（P4.3 / P4.4 / P4.2）|

## 阶段进度

- ✅ **H0** 设计文档 + spec/changes 四件套（已归档于 `spec/changes/add-embedding-api/`）
- 🟡 **H1** Tier 1 C ABI scaffold —— 单实例 lifecycle 跑通、build matrix（default / interp-only / ios / android）全绿、12 个 unit test
- 📋 **H2** load_zbc / resolve_entry / invoke 全链路 + stdout sink 接 VM + Tier 2 Rust + C/Rust example
- 📋 **H3** 错误路径全覆盖 + VM exception 翻译
- 📋 **H4** 移动平台 facade 接入（归 `add-platform-ios` / `add-platform-android` spec）
- 📋 **H5** test-runner library 重构到 `z42-host` 之上（归 runner spec）

## 依赖关系

- 依赖 `runtime/`（host 模块在 `src/runtime/src/host/` 内，通过 `extern "C"` 暴露给宿主）
- Tier 2 / Tier 3 / examples 依赖 Tier 1 C ABI；不直连 VM internals
- 被外部应用消费（非 z42 编译器 / VM 内部使用）

## 与 interop 的边界

| 方向 | 解决问题 | 文档 |
|------|---------|------|
| **interop** | native 代码 → 注册类型/方法进 z42（CPython C 扩展类比）| [docs/design/interop.md](../../../docs/design/interop.md) |
| **host (本模块)** | 宿主 app → 启动 VM → 加载 .zbc → 调用入口 → 关闭（CoreCLR `coreclrhost.h` 类比）| [docs/design/embedding.md](../../../docs/design/embedding.md) |

两者复用同一份 `Z42Value` / `Z42Args` / `Z42Error` 类型定义（在 `z42_abi.h`），互不重叠。
