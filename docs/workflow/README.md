# docs/workflow/

**面向开发者：怎么跑命令。** 设计原理归 [`docs/design/`](../design/)；spec 提案流程归 [`.claude/rules/workflow.md`](../../.claude/rules/workflow.md)。

## 前置：先拿到 z42

所有命令都经 `z42` launcher 跑，而 z42 的工具链本身用 z42 写（`xtask`）——所以先下载一个预编译 launcher 引导（鸡生蛋的唯一原生 primer）：

```bash
./scripts/install-z42.sh                       # → ./.z42/（z42 launcher + z42c + z42vm + stdlib）；Windows: install-z42.bat
export PATH="$PWD/.z42:$PWD/.z42/bin:$PATH"     # z42 / z42c / z42vm 上 PATH
z42c build scripts/xtask.z42.toml --release    # 首次构建 dev CLI → artifacts/xtask/xtask.zpkg
```

> 从源码整套构建（不下预编译）见 [`building/`](building/)；冷启动 bootstrap 机制见 [`building/stdlib.md`](building/stdlib.md)。

## Quick Start

```bash
z42 xtask.zpkg build all    # 编译器 + VM + stdlib
z42 xtask.zpkg test         # 全部测试
```

完整命令：`z42 xtask.zpkg help`（源 = [`scripts/xtask*.z42`](../../scripts/)）。

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
| **在 Windows 上跑 xtask** | [`windows.md`](windows.md) |
| 打跨平台 release | [`release.md`](release.md) |
| lldb / gdb / dap 调试 | [`debugging.md`](debugging.md) |

## artifacts/ 目录速查

```
artifacts/
├── build/
│   ├── compiler/<proj>/bin/             dotnet build 产物（z42c.dll）
│   ├── runtime/{debug,release}/         cargo build 产物（z42vm）
│   └── libraries/
│       ├── <lib>/release/dist/          per-lib workspace .zpkg
│       └── dist/release/                flat 视图（namespace→zpkg）+ index.json，VM 默认加载路径
└── xtask/xtask.zpkg                      编译后的 dev CLI
```

`artifacts/` 和 `target/` 都 gitignore。
