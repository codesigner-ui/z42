# z42c.driver

## 职责
镜像 C# [z42.Driver](../../compiler/z42.Driver/README.md)：CLI 入口（命令路由）。唯一 **exe** 子包，对外别名 = 用户 `z42c` 命令。绝不 fallback 到 dotnet z42c.dll。命令逐子版本解锁：**lex/parse 前端已实现**（`--dump-tokens`/`--dump-ast`，调用 z42c.syntax `DumpTool`）；manifest-check / build 待后端落地。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/Main.z42` | `void Main()`：读 `Environment.GetCommandLineArgs()` + `File.ReadAllText`，路由 `--dump-tokens`/`--dump-ast` 到 `DumpTool`（`namespace Z42.Driver`）|

## 入口点
`Z42.Driver.Main`（auto-detected exe 入口）。
用法：`z42c --dump-tokens <file.z42>` / `z42c --dump-ast <file.z42>`。

## 依赖关系
→ z42c.syntax, z42c.core。stdlib（Std / Std.IO）自动可用。

## 运行（自举产物）
需把 z42c 7 包 + stdlib 合并到**单个** flat 目录再 `Z42_LIBS=<该目录>`（见 self-hosting.md 的 Z42_LIBS 单目录陷阱）：
```
Z42_LIBS=<flat> z42vm <flat>/z42c.driver.zpkg -- --dump-ast <file.z42>
```
