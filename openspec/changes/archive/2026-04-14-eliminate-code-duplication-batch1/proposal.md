# Proposal: 消除编译器代码重复（批次 1）

## Why

代码审查发现编译器中存在多处重复代码（H1/M3/M4/M6），包括工具方法、符号判断逻辑和 stdlib 命名空间提取逻辑分别在 2-3 个位置各自实现。这增加了维护成本，且已出现不一致（M3 中 SingleFileCompiler 使用 `"z42."` 前缀而 GoldenTests 使用 `"Std."` 前缀）。

## What Changes

- **H1**: `Sha256Hex` 从 PackageCompiler + SingleFileCompiler 提取到共享工具类
- **M6**: `IsObjectClass` 从 TypeChecker.Classes + IrGen 提取到共享静态类
- **M4**: 删除 GoldenTests + ZbcRoundTripTests 中的 `BuildStdlibIndex` 副本，改用 `PackageCompiler.BuildStdlibIndex`
- **M3**: SingleFileCompiler 和 GoldenTests 的 stdlib 命名空间提取改为使用 `IrGen.UsedStdlibNamespaces`（已有的正确实现），消除手动 IR 扫描

## Scope（允许改动的文件/模块）

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `z42.Pipeline/CompilerUtils.cs` | 新增 | 共享工具类（Sha256Hex） |
| `z42.IR/WellKnownNames.cs` | 新增 | 共享常量/判断（IsObjectClass） |
| `z42.Pipeline/PackageCompiler.cs` | 修改 | 删除 Sha256Hex，改用 CompilerUtils |
| `z42.Pipeline/SingleFileCompiler.cs` | 修改 | 删除 Sha256Hex + 手动命名空间提取，改用 CompilerUtils + IrGen.UsedStdlibNamespaces |
| `z42.Semantics/TypeCheck/TypeChecker.Classes.cs` | 修改 | 删除 IsObjectClass，改用 WellKnownNames |
| `z42.Semantics/Codegen/IrGen.cs` | 修改 | 删除 IsObjectClass，改用 WellKnownNames |
| `z42.Tests/GoldenTests.cs` | 修改 | 删除 BuildIndexFromDir + GetUsedStdlibNamespaces |
| `z42.Tests/ZbcRoundTripTests.cs` | 修改 | 删除 BuildStdlibIndexFromDir |

## Out of Scope

- Parser 错误恢复（A5）
- SemanticModel 引入（A1）
- H2（ParseException 统一）/ H4（CompileFile/CheckFile 重构）— 属于批次 2
- 运行时性能优化（H5/M2）— 属于批次 4

## Open Questions

- 无
