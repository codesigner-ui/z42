# z42.Driver — CLI 入口

## 职责

命令行入口，负责命令路由和构建编排。依赖 z42.Compiler、z42.IR、z42.Project 三者全部。

## 核心文件

| 文件 | 职责 |
|------|------|
| `Program.cs` | 命令路由（手写 argv 解析）；单文件模式完整 pipeline：Lex → Parse → TypeCheck → Codegen → Emit |
| `BuildCommand.cs` | 项目模式构建编排：加载清单 → 路由单目标/多目标 → 逐文件编译 → 组装 `.zpkg` |

## 入口点

- `Program.Main(args)` — CLI 入口，支持命令：`build`、`check`、`disasm`、`explain`、`errors`，以及直接传入 `.z42` 文件

## 依赖关系

依赖 `z42.Compiler` + `z42.IR` + `z42.Project`（最顶层，无被依赖方）。
