# C# 编译器单元测试

z42 编译器层 xUnit 测试在 [`src/compiler/z42.Tests/`](../../../src/compiler/z42.Tests/)。

## 命令

```bash
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
```

或经 xtask：

```bash
z42 xtask.zpkg test compiler
```

## 跑单个 / 部分 test

```bash
# 按完全限定名
dotnet test src/compiler/z42.Tests/z42.Tests.csproj \
  --filter 'FullyQualifiedName=Z42.Tests.TypeCheckerTests.FuncConstraint_NamedDelegate_Passes'

# 按子串
dotnet test src/compiler/z42.Tests/z42.Tests.csproj \
  --filter 'FullyQualifiedName~FuncConstraint'

# 按类（适配 partial class）
dotnet test src/compiler/z42.Tests/z42.Tests.csproj \
  --filter 'FullyQualifiedName~Z42.Tests.GoldenTests'
```

## 详细输出

```bash
dotnet test src/compiler/z42.Tests/z42.Tests.csproj --logger 'console;verbosity=detailed'
dotnet test src/compiler/z42.Tests/z42.Tests.csproj --logger 'console;verbosity=normal'
```

## 测试类别

| 测试类 | 覆盖 |
|------|------|
| `LexerTests.cs` | Lexer + token 规则 |
| `ParserTests.*` | 各语法构造解析 |
| `TypeCheckerTests.*` (partial) | TypeChecker（含 constraints / generics 等）|
| `IrGenTests.cs` | IR codegen |
| `GoldenTests.cs` | `src/tests/**` 跑 z42c 编译产物对比 |
| `ZbcRoundTripTests.cs` | zbc 二进制 reader/writer |
| `WorkspaceTests.*` | 工程文件 / workspace |
| `R*Tests` / `Test*` | R 系列测试基础设施 |

## 加新测试

参见 [`.claude/rules/workflow.md`](../../../.claude/rules/workflow.md) "测试要求" + [`docs/design/testing/testing.md`](../../design/testing/testing.md) "编写新测试" 段。

新文件命名 `<Topic>Tests.cs`，放 [`src/compiler/z42.Tests/`](../../../src/compiler/z42.Tests/)；按 `partial class TypeCheckerTests` 或类似模式合并到现有类。
