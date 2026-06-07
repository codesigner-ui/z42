# z42c.driver

## 职责
镜像 C# [z42.Driver](../../compiler/z42.Driver/README.md)：CLI 入口（命令路由）。唯一 **exe** 子包，对外别名 = 用户 `z42c` 命令。**B0 骨架：`Main()` 仅打印 banner，无桥接**（不实现任何命令、绝不 fallback 到 dotnet z42c.dll）。命令逐子版本解锁（lex/parse/manifest-check 0.3.4；build 0.3.9）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/Main.z42` | `void Main()` banner（`namespace Z42.Driver`，引用 pipeline/core/ir 验证 exe→lib 链接）|

## 入口点
`Z42.Driver.Main`（auto-detected exe 入口）。

## 依赖关系
→ z42c.pipeline, z42c.ir, z42c.core。stdlib 自动可用。
