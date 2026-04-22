# toolchain/host — z42 宿主集成

## 职责

将 z42 VM 嵌入外部宿主环境的集成层。提供稳定的 C ABI / Rust API，供 IDE、GUI 应用、插件系统或其他运行时加载和调用 z42 模块。

不做：VM 执行引擎本身（归 `runtime/`）、调试协议（归 `debugger/`）。

## 计划模块

尚未实现。预期包含：

| 模块 | 职责 |
|------|------|
| `ffi/` | C ABI 导出（加载 `.zpkg`、执行入口、值转换） |
| `embed/` | Rust 原生嵌入 API（`Host::new()` / `Host::invoke()`） |
| `examples/` | 最小宿主示例（C / Rust） |

## 依赖关系

- 依赖 `runtime/`（调用 VM 执行）
- 被外部应用消费（非 z42 编译器/VM 内部使用）
