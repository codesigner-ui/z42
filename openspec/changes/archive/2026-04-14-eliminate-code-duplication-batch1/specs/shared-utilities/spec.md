# Spec: 共享工具类与重复代码消除

## ADDED Requirements

### Requirement: CompilerUtils 共享工具类

#### Scenario: Sha256Hex 统一入口
- **WHEN** PackageCompiler 或 SingleFileCompiler 需要计算源文件哈希
- **THEN** 调用 `CompilerUtils.Sha256Hex(text)` 而非各自的私有方法
- **THEN** 返回格式为 `"sha256:<lowercase-hex>"`

### Requirement: WellKnownNames 共享常量

#### Scenario: IsObjectClass 统一判断
- **WHEN** TypeChecker 或 IrGen 需要判断一个类名是否为 Object 基类
- **THEN** 调用 `WellKnownNames.IsObjectClass(name)` 而非各自的私有方法
- **THEN** 对 `"Object"` 和 `"Std.Object"` 返回 `true`，其他返回 `false`

## MODIFIED Requirements

### Requirement: Stdlib 命名空间提取统一

**Before:** SingleFileCompiler 通过扫描 IR 中的 `CallInstr` 提取 `"z42."` 前缀函数名来推断 stdlib 命名空间；GoldenTests 用相同方式但匹配 `"Std."` 前缀。两者逻辑不一致。

**After:** SingleFileCompiler 和 GoldenTests 均使用 `IrGen.UsedStdlibNamespaces` 属性获取在代码生成过程中实际引用的 stdlib 命名空间列表，不再从 IR 指令中重新提取。

### Requirement: BuildStdlibIndex 统一

**Before:** GoldenTests.BuildIndexFromDir 和 ZbcRoundTripTests.BuildStdlibIndexFromDir 各自实现了与 `PackageCompiler.BuildStdlibIndex` 相同的逻辑。

**After:** 测试代码直接调用 `PackageCompiler.BuildStdlibIndex(new[] { libsDir })`，删除本地副本。

## Pipeline Steps

受影响的 pipeline 阶段：
- [ ] Lexer
- [ ] Parser / AST
- [x] TypeChecker（IsObjectClass 引用变更）
- [x] IR Codegen（IsObjectClass 引用变更 + UsedStdlibNamespaces 暴露方式）
- [ ] VM interp
