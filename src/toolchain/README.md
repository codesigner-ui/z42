# src/toolchain — z42 配套工具链

## 职责

围绕 `compiler/` 与 `runtime/` 的配套工具集合：宿主集成、调试器、应用打包、端到端工作流。不包含语言核心（编译器、VM）与标准库源码（`libraries/`）。

## 子目录

| 目录 | 职责 | 状态 |
|------|------|:----:|
| [launcher/](launcher/) | `z42` launcher（muxer）：原生 trampoline + `launcher.zpkg`（run/link/list/install/export…）+ per-app 原生 apphost（`apphost.z42` patch 库，经 `z42 export desktop`）。类比 `dotnet` muxer + `rustup` | ✅ 已实装 |
| [test-runner/](test-runner/) | `z42-test-runner`：跑 stdlib / 工程的 `[Test]` / `[Benchmark]`，输出 TAP（`xtask test` 内嵌调用） | ✅ 已实装 |
| [debugger/](debugger/) | z42 程序调试器（断点、单步、变量查看） | 占位 |
| [packager/](packager/) | 应用打包与发行（将 z42 程序 + 运行时打成独立可分发产物） | 占位 |
| [workload/](workload/) | 平台相关能力束（consolidate-platform-into-workload）：`host-api/`（Tier 2 `z42-host` crate）+ `platforms/{ios,android,wasm,desktop}/`（facade + 测试）；按需 `z42 workload install`。host/ 解散后承接 | 🚧 实装中 |

> 命名说明：`toolchain` 取"围绕 compiler/runtime 的整套配套工具"之广义；语言核心**编译器在 [`../compiler/`](../compiler/) + [`../z42c/`](../z42c/)**、VM 在 [`../runtime/`](../runtime/)，不在本目录。

## 状态

launcher / test-runner 已实装并在 CI / xtask 中使用；workload 实装中（承接 host 解散迁入的 host-api + 平台 facade，consolidate-platform-into-workload）；debugger / packager 为占位，具体设计与落地时机见 `docs/roadmap.md`。`host/` 顶层已移除——Tier 1 C ABI + 头在 [`../runtime/src/host/`](../runtime/src/host/) + [`../runtime/include/`](../runtime/include/)，Tier 2/Tier 3 在 `workload/`。

> launcher 的演进方向（命令分发三层、平台工程导出、runtime/workload 分发）见 [`docs/design/toolchain/`](../../docs/design/toolchain/)。

## 依赖关系

- 消费：`compiler/`（调用 CLI 或 API）、`runtime/`（嵌入或调用 VM）
- 被消费：`scripts/`（发行与测试脚本可能调用 packager / workload）
