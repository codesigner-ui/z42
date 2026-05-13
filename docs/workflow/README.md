# docs/workflow/

**面向开发者：怎么跑命令。** 设计原理归 [`docs/design/`](../design/)；spec 提案流程归 [`.claude/rules/workflow.md`](../../.claude/rules/workflow.md)。

## Quick Start

```bash
just build          # 编译器 + VM
just test           # 全部测试
just clean          # 清 artifacts/
```

完整命令：[`justfile`](../../justfile)。

## 我要做 ... → 看 ...

| 我要 | 看 |
|------|-----|
| 编 C# 编译器 | [`building/compiler.md`](building/compiler.md) |
| 编 Rust VM | [`building/vm.md`](building/vm.md) |
| 重建 stdlib | [`building/stdlib.md`](building/stdlib.md) |
| 桌面跨平台 build | [`building/cross-platform.md`](building/cross-platform.md) |
| 嵌入 z42 到 **WASM** | [`building/wasm.md`](building/wasm.md) |
| 嵌入 z42 到 **iOS** | [`building/ios.md`](building/ios.md) |
| 嵌入 z42 到 **Android** | [`building/android.md`](building/android.md) |
| 跑 C# xUnit | [`testing/unit-tests.md`](testing/unit-tests.md) |
| 跑 VM golden tests | [`testing/vm-tests.md`](testing/vm-tests.md) |
| 跑 stdlib `[Test]` | [`testing/stdlib-tests.md`](testing/stdlib-tests.md) |
| 跑 cross-zpkg e2e | [`testing/cross-zpkg.md`](testing/cross-zpkg.md) |
| 只跑 git diff 影响的测试 | [`testing/changed-only.md`](testing/changed-only.md) |
| 看 CI matrix / GREEN 标准 | [`ci.md`](ci.md) |
| **本地打 9 个 per-arch SDK package** | [`packaging.md`](packaging.md) |
| 打跨平台 release | [`release.md`](release.md) |
| lldb / gdb / dap 调试 | [`debugging.md`](debugging.md) |

## artifacts/ 目录速查

```
artifacts/
├── compiler/<proj>/bin/      dotnet build 产物（z42c.dll）
├── rust/{debug,release}/     cargo build 产物（z42vm）
├── libraries/<lib>/dist/     stdlib workspace .zpkg
└── z42/
    ├── bin/                  分发版 z42c + z42vm
    └── libs/<lib>.zpkg       VM 默认加载路径
```

`artifacts/` 和 `target/` 都 gitignore。
