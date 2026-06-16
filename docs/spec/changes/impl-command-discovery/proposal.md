# impl-command-discovery — proposal（B1）

> 状态：**DRAFT**（2026-06-17）。build-workload-subsystem 的 B1：实现 [launcher-command-dispatch.md](../../../design/toolchain/launcher-command-dispatch.md) 的命令发现机制。
> 子系统锁：`toolchain`。

## Why

launcher-command-dispatch.md 设计了三层命令：**Core**（launcher baked）/ **SDK**（随 runtime，目录发现）/ **Workload**（按需，目录发现）。现状：**全部 baked**（export/publish 写死在 launcher_cli.z42）。要让 workload 平台命令（ios/android/wasm 工具）按需装入即可用、不重编 launcher，需要"目录发现 + 注册进 Std.Cli 树"机制。这是 B2/B4/B5 的地基。

## ⚠️ 先有鸡蛋问题（要先对齐）

**B1 单独做 = 建一套"发现机制"但现在没有可发现的命令**——`build`=z42c、`new`/`test`/`fmt` 还不是 launcher 命令、`export`/`publish` 现 baked。发现机制做出来会"发现到 0 个命令"，直到后续阶段把命令打成可发现 zpkg。

→ 所以 B1 应**带一个真实迁移作证**，二选一（见 design.md 决策）：
- **方案 X**：把现 baked 的 `export`/`publish` 从 launcher_cli.z42 迁成**发现式**命令（立即用上发现机制 + 减 launcher 体积）。
- **方案 Y**：B1 只做机制 + 一个 test-only 发现命令作证，真实命令迁移留后续。

## What

1. 命令目录布局（SDK 版本作用域 + workload）。
2. 命令 manifest（name/description/usage，供 `z42 -h` 列出）。
3. Std.Cli 注册：发现的命令注册进 SubcommandRouter（与 baked core 同树），dispatch = spawn `z42vm <cmd.zpkg> -- <argv>`。
4. 优先级：core baked 优先；发现命令不得遮蔽保留名。

## Scope（待 design 决策定稿后细化）

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/toolchain/launcher/core/launcher_discovery.z42` | NEW | 扫描命令目录 + 读 manifest + 注册 |
| `src/toolchain/launcher/core/launcher_cli.z42` | MODIFY | _launcherRoot 调发现；dispatch spawn 发现命令 |
| `src/libraries/z42.cli/...` | MODIFY? | SubcommandRouter 若需"spawn 式"叶子支持 |
| docs/design/toolchain/launcher-command-dispatch.md | MODIFY | 落地目录布局 + manifest schema |
| 测试 | NEW | 发现 + dispatch 的 [Test] / e2e |

## Out of Scope

- workload 打包 + `z42 workload install`（B2）。
- 把 build/new/test 做成 SDK 命令（后续；它们现在不是 launcher 命令）。

## Open Questions（design.md 决策，需 User 拍）

- [ ] 鸡蛋问题：方案 X（迁 export/publish 作证）还是 Y（test-only 作证）？
- [ ] 命令目录布局 + manifest 形态（见 design.md 推荐）。
- [ ] SubcommandRouter 是否需扩"spawn 式"叶子（vs 在 dispatch 层特判）。
