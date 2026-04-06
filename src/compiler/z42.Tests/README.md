# z42.Tests — 测试套件

## 职责

覆盖编译器各阶段及关键数据格式的自动化测试。包含单元测试和端到端 golden tests。

## 核心文件

| 文件 | 覆盖范围 |
|------|---------|
| `LexerTests.cs` | Token 识别、关键字、符号、数字/字符串字面量 |
| `ParserTests.cs` | AST 节点构造、组合子行为、错误恢复 |
| `TypeCheckerTests.cs` | 类型推断、运算符类型表、错误报告 |
| `IrGenTests.cs` | 字节码生成、寄存器分配、调用类型（static / virtual / builtin） |
| `GoldenTests.cs` | 端到端 pipeline：源文件 → 参考输出对比 |
| `GrammarSyncTests.cs` | 语法定义一致性校验 |
| `ProjectManifestTests.cs` | TOML 解析、多目标、glob 展开 |
| `ZbcRoundTripTests.cs` | `ZbcWriter` → `ZbcReader` 字节级往返验证 |
| `ZpkgNamespacesTests.cs` | 命名空间在包格式中的序列化验证 |

## 运行方式

```bash
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
```
