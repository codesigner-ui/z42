# Proposal: PackageCompiler 统一重构（批次 2）

## Why

PackageCompiler 存在三个相关问题：H2（ParseException 错误码错误）、H4（CompileFile/CheckFile 重复）、L6（BuildTarget 过长）。三者集中在同一文件的相邻代码段，一次重构比分三次修改更安全。

## What Changes

- **H4**: 提取 `TryParseAndCheck` 私有方法，消除 `CompileFile`/`CheckFile` ~95% 重复代码
- **H2**: 在 `TryParseAndCheck` 中将 `ParseException` 写入 `DiagnosticBag`，使用 `DiagnosticCodes.UnexpectedToken`（E0201）替换硬编码 `"E0001"`
- **L6**: 拆分 `BuildTarget`（139行）为四个内聚私有方法

## Scope

| 文件 | 变更类型 |
|------|---------|
| `z42.Pipeline/PackageCompiler.cs` | 修改（唯一改动文件） |

## Out of Scope

- SingleFileCompiler 的 ParseException 处理（同类问题，但已在单独 issue 中追踪）
- DiagnosticBag.PrintAll() 返回类型改造（批次 3 L5）
- 其他 pipeline 组件
