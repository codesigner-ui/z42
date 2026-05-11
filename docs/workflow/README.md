# docs/workflow/

z42 项目的构建、测试、发布、调试工作流。**面向开发者；如何做 X。**

> **与 [`docs/design/`](../design/) 的关系**：design 描述"为什么这样设计"；workflow 描述"如何运行命令"。例：`design/testing/testing.md` 讲 R 系列测试基础设施的设计；`workflow/testing/vm-tests.md` 讲 `./scripts/test-vm.sh` 怎么用。

## Quick Start

```bash
just                # 列出所有 task
just build          # 编译器 + 运行时
just test           # 全部测试（compiler + VM + cross-zpkg）
just test-vm        # 仅 VM golden tests（最快迭代回路）
just clean          # 清空 artifacts/
just ci             # CI 标准管线
```

完整 `just` 命令清单见 [`justfile`](../../justfile)；详细各场景见下表。

## 决策树："我想做 X → 看哪个"

| 我想 ... | 看 |
|---------|-----|
| 编译 C# 编译器 | [building/compiler.md](building/compiler.md) |
| 编译 Rust VM | [building/vm.md](building/vm.md) |
| 重新构建标准库（改了 `src/libraries/*` 后） | [building/stdlib.md](building/stdlib.md) |
| 在 macOS / Linux / Windows / arm64 跑 | [building/cross-platform.md](building/cross-platform.md) |
| 跑 C# 编译器 xUnit 测试 | [testing/unit-tests.md](testing/unit-tests.md) |
| 跑 VM golden tests（interp / JIT） | [testing/vm-tests.md](testing/vm-tests.md) |
| 跑 stdlib 内部测试（`[Test]` 注解） | [testing/stdlib-tests.md](testing/stdlib-tests.md) |
| 跑 cross-zpkg 端到端测试 | [testing/cross-zpkg.md](testing/cross-zpkg.md) |
| 只跑被 git diff 影响到的测试 | [testing/changed-only.md](testing/changed-only.md) |
| 理解 CI 矩阵 / GREEN 标准 | [ci.md](ci.md) |
| 打跨平台 release | [release.md](release.md) |
| 用 lldb / gdb / dap 调试 VM / 用户代码 | [debugging.md](debugging.md) |

## 与 spec workflow 的关系

代码 / spec 协作流程（`spec/changes/<name>/` 提案、阶段 1-9）见 [`.claude/rules/workflow.md`](../../.claude/rules/workflow.md) —— 本目录只讲"如何运行 build/test 命令"，不讲"如何提一个新 feature 的 spec"。

## artifacts 目录分工

```
artifacts/
├── compiler/<proj>/bin/         # dotnet build 产物（z42c.dll 等）
├── rust/{debug,release}/        # cargo build 产物（z42vm 等）
├── libraries/<lib>/             # stdlib workspace 构建产物
│   ├── dist/<lib>.zpkg
│   └── cache/*.zbc              # debug/indexed 模式 中间产物
└── z42/
    ├── bin/                     # 分发版 z42c + z42vm
    └── libs/<lib>.zpkg          # VM 默认加载路径（build-stdlib.sh 自动 sync）
```

`artifacts/` 与 `target/` 均 gitignore，不纳入版本控制。
