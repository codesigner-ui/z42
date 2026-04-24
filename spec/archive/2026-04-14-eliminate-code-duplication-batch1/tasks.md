# Tasks: 消除编译器代码重复（批次 1）

> 状态：🟢 已完成 | 创建：2026-04-14 | 完成：2026-04-14

**变更说明：** 提取 4 处重复代码到共享位置，统一 stdlib 命名空间提取方式
**原因：** 代码审查发现 H1/M3/M4/M6 四处重复，其中 M3 已出现前缀不一致 bug
**文档影响：** 无（纯内部重构，不改外部行为）

## 进度概览
- [x] 阶段 1: 新建共享基础设施
- [x] 阶段 2: 迁移调用方
- [x] 阶段 3: 验证

## 阶段 1: 新建共享基础设施
- [x] 1.1 新建 `z42.Pipeline/CompilerUtils.cs`：包含 `Sha256Hex` 公共静态方法
- [x] 1.2 新建 `z42.IR/WellKnownNames.cs`：包含 `IsObjectClass` 公共静态方法

## 阶段 2: 迁移调用方
- [x] 2.1 [H1] PackageCompiler.cs：删除私有 Sha256Hex，改用 CompilerUtils.Sha256Hex
- [x] 2.2 [H1] SingleFileCompiler.cs：删除私有 Sha256Hex，改用 CompilerUtils.Sha256Hex
- [x] 2.3 [M6] TypeChecker.Classes.cs：删除私有 IsObjectClass，改用 WellKnownNames.IsObjectClass
- [x] 2.4 [M6] IrGen.cs：删除私有 IsObjectClass，改用 WellKnownNames.IsObjectClass
- [x] 2.5 [M4] GoldenTests.cs：删除 BuildIndexFromDir，改用 PackageCompiler.BuildStdlibIndex
- [x] 2.6 [M4] ZbcRoundTripTests.cs：删除 BuildStdlibIndexFromDir，改用 PackageCompiler.BuildStdlibIndex
- [x] 2.7 [M3] SingleFileCompiler.cs：保留 IrGen 引用，用 UsedStdlibNamespaces 替换手动 IR 扫描
- [x] 2.8 [M3] GoldenTests.cs：Compile() 返回 UsedStdlibNamespaces，替换 GetUsedStdlibNamespaces

## 阶段 3: 验证
- [x] 3.1 dotnet build src/compiler/z42.slnx —— 无编译错误
- [x] 3.2 dotnet test src/compiler/z42.Tests/z42.Tests.csproj —— 395 passed
- [x] 3.3 cargo build --manifest-path src/runtime/Cargo.toml —— 无编译错误
- [x] 3.4 ./scripts/test-vm.sh —— 114 passed (57 interp + 57 jit)

## 备注
- M3 修复了实际 bug：SingleFileCompiler 用 "z42." 前缀扫描（错误），GoldenTests 用 "Std." 前缀扫描（接近正确但仍是冗余实现）。现在统一使用 IrGen.UsedStdlibNamespaces 作为唯一来源。
