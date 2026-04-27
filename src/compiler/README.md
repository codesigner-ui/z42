# z42 Compiler — 模块说明

C# Bootstrap 编译器，由 8 个项目组成。每个子项目的核心文件表、入口点、内部
拆分细节由其 **自身的 `README.md`** 负责，本文件只承担"模块职责一览 + 依赖
关系"，避免与子 README 漂移（参见 [`.claude/rules/code-organization.md`](../../.claude/rules/code-organization.md) "每个目录都要有 README"）。

## 模块职责

| 模块 | 一句话职责 | 详细 |
|------|-----------|------|
| **`z42.Core`** | 基础设施：诊断、Span、LanguageFeatures | [README](z42.Core/README.md) |
| **`z42.Syntax`** | 语法层：Lexer + Parser → AST | [README](z42.Syntax/README.md) |
| **`z42.Semantics`** | 语义层：TypeCheck（含 Bound 节点）+ Codegen | [README](z42.Semantics/README.md) |
| **`z42.IR`** | 共享契约：IR 模型 + zbc 二进制格式 + 项目类型 | [README](z42.IR/README.md) |
| **`z42.Project`** | 项目清单：`.z42.toml` 解析、源文件发现、zpkg builder | [README](z42.Project/README.md) |
| **`z42.Pipeline`** | 编译管线：单文件 + 包级别编译编排 | [README](z42.Pipeline/README.md) |
| **`z42.Driver`** | CLI 入口：命令路由（`build` / `check` / `disasm` / `explain` / …） | [README](z42.Driver/README.md) |
| **`z42.Tests`** | 测试套件：单元 + golden + ZbcRoundTrip | [README](z42.Tests/README.md) |

`z42.Core` 与 `z42.IR` 是仅有的两个无内部依赖层，所有其他模块均可安全引用。

## 依赖关系（邻接表）

| 模块 | 依赖 |
|------|------|
| `z42.Core` | — |
| `z42.IR` | — |
| `z42.Syntax` | `z42.Core` |
| `z42.Semantics` | `z42.Core` + `z42.Syntax` + `z42.IR` |
| `z42.Project` | `z42.IR` |
| `z42.Pipeline` | `z42.Core` + `z42.Syntax` + `z42.Semantics` + `z42.IR` + `z42.Project` |
| `z42.Driver` | `z42.Pipeline` + `z42.IR` + `z42.Core` |
| `z42.Tests` | （所有上述模块）|

> 邻接表替代了原 ASCII 箭头图：箭头图存在 `z42.Driver` 文字描述与图不符、
> `z42.Tests` 线条无终点、`z42.Semantics → z42.IR` 视觉断开等多处歧义
> （review1 §一.1）。

## 编译数据流

```
SourceText ──→ Lexer ──→ Token[] ──→ Parser ──→ CompilationUnit (AST)
                                                          │
                                                          ↓
                                              SymbolCollector (Pass 0)
                                                          │
                                                          ↓
                                                TypeChecker (Pass 1)
                                                          │
                                                          ↓ SemanticModel
                                                          │
                                                          ↓
                                                  IrGen (Lowering)
                                                          │
                                                          ↓ IrModule
                                                          │
                                                          ↓
                                              ZbcWriter / ZpkgBuilder
                                                          │
                                                          ↓
                                                 .zbc / .zpkg (binary)
```

## 阅读顺序建议

1. 看本文件 → 找到目标模块
2. 跳进子 README → 拿到核心文件表与入口点
3. 必要时再读源码

不要从顶层 README 找具体的类/方法 —— 那是子 README 的职责。
