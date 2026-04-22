# src/toolchain — z42 配套工具链

## 职责

围绕 `compiler/` 与 `runtime/` 的配套工具集合：宿主集成、调试器、应用打包、端到端工作流。不包含语言核心（编译器、VM）与标准库源码（`libraries/`）。

## 子目录

| 目录 | 职责 |
|------|------|
| [host/](host/) | 将 z42 VM 嵌入外部宿主（IDE、GUI、其他进程）的集成层 |
| [debugger/](debugger/) | z42 程序调试器（断点、单步、变量查看） |
| [packager/](packager/) | 应用打包与发行（将 z42 程序 + 运行时打成独立可分发产物） |
| [workload/](workload/) | 端到端工作流与发行场景测试 |

## 状态

占位目录，各子项尚未开始实现。具体设计与落地时机见 `docs/roadmap.md`。

## 依赖关系

- 消费：`compiler/`（调用 CLI 或 API）、`runtime/`（嵌入或调用 VM）
- 被消费：`scripts/`（发行与测试脚本可能调用 packager / workload）
