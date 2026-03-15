---
name: parse-test
description: 对 z42 源文件运行词法分析或语法分析，显示 token 列表或解析结果。在用户说"测试解析"、"dump tokens"、"看看这个文件能不能解析" 时触发。
user-invocable: true
allowed-tools: Bash(dotnet run*), Read
argument-hint: <file.z42> [--tokens|--parse]
---

# 解析测试：$ARGUMENTS

## 词法分析（查看 token 流）

```bash
dotnet run --project src/compiler/Z42.Driver -- $1 --dump-tokens
```

## 语法分析（查看解析结果）

```bash
dotnet run --project src/compiler/Z42.Driver -- $1
```

成功输出格式：
```
Parsed module '<name>' with <N> items.
```

失败输出格式：
```
parse error at <line>:<col>: <message>
```

## 如果参数为空，使用示例文件

```bash
dotnet run --project src/compiler/Z42.Driver -- examples/hello.z42 --dump-tokens
```

先读取要测试的文件，再运行上述命令，然后将输出结果展示给用户并分析是否符合预期。
