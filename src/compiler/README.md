# z42 Compiler — 模块说明

```
z42.IR          共享契约层（IR 模型 + 包格式 + 项目类型）
z42.Compiler    编译器核心管线（Lexer → Parser → TypeCheck → Codegen）
z42.Project       项目清单管理（.z42.toml 解析 + 源文件发现）
z42.Driver      CLI 入口（命令路由 + 构建编排）
z42.Tests       测试套件
```

---

## z42.IR — 共享契约层

**依赖方向**：被 z42.Compiler、z42.Project、z42.Driver 共同引用；自身不依赖任何其他 z42 项目。

这个模块有三个职责，彼此独立但都属于"编译器与运行时之间的契约"：

### 1. IR 模型（`IrModule.cs`）

编译器输出、VM 输入的核心数据结构。SSA 寄存器形式，与 Rust VM 的 `metadata::IrModule` 一一对应。

```
IrModule
├── StringPool      全局字符串去重表
├── ClassDescriptors 类型描述（字段布局）
└── Functions[]
    ├── Parameters / Locals / Registers
    └── Blocks[]
        └── Instructions[]   — SSA 指令序列
```

### 2. 包格式类型（`PackageTypes.cs`）

编译产物的数据模型，以 JSON 序列化（Phase 1）、Phase 2 改为二进制：

| 类型 | 文件 | 说明 |
|------|------|------|
| `ZbcFile` | `.zbc` | 单个 `.z42` 源文件的编译结果 |
| `ZpkgFile` | `.zpkg` | 项目级包，含所有 ZbcFile（packed 模式）或引用路径列表（indexed 模式）|
| `ZpkgKind` | — | `Exe` / `Lib` |
| `ZpkgMode` | — | `Indexed`（开发增量）/ `Packed`（可分发）|

### 3. 项目类型（`ProjectTypes.cs`）

`z42.toml` 的反序列化模型（Tomlyn TOML 库的目标类型），同时也供 z42.Project 的解析逻辑使用：

| 类型 | 对应 TOML 节 | 说明 |
|------|------------|------|
| `Z42Proj` | `[project]` + `[sources]` + `[build]` | 单项目清单根对象 |
| `Z42Sln` | `[workspace]` | 多项目 workspace 清单根对象 |
| `ProjectMeta` | `[project]` | 名称、版本、kind、入口等 |
| `ProfileConfig` | `[profile.<name>]` | 构建优化选项，可按 debug/release 覆盖 |

### 4. 二进制格式（`BinaryFormat/`）

| 文件 | 功能 |
|------|------|
| `Opcodes.cs` | 字节码指令集定义（`0x00`–`0x8F`，约 40 条） |
| `ZbcWriter.cs` | `IrModule` → `.zbc` 二进制序列化 |
| `ZbcReader.cs` | `.zbc` → `IrModule` 反序列化 |
| `ZasmWriter.cs` | `IrModule` → `.zasm` 文本汇编（调试用） |

---

## z42.Compiler — 编译器核心管线

**只依赖 z42.IR**，不依赖 z42.Project 或 z42.Driver。

实现从源码到 IR 的完整变换：

```
源码字符串
  ↓  Lexer        规则驱动的 tokenizer（LexCombinators + TokenDefs）
Token 流
  ↓  Parser       手写组合子 + Pratt 表达式解析 → AST（sealed record 节点）
CompilationUnit（AST）
  ↓  TypeChecker  两遍检查：Pass 0 收集类型形状，Pass 1 检查函数体
类型检查后的 AST
  ↓  IrGen        生成 SSA IrModule，含寄存器分配和块标签
IrModule
```

各子目录职责：

| 目录 | 职责 |
|------|------|
| `Lexer/` | `TokenDefs`（规则声明）+ `LexCombinators`（组合子）+ `Lexer`（通用引擎） |
| `Parser/Core/` | `TokenCursor`（不可变游标）、`ParseResult<T>`、`Combinators`（组合子函数） |
| `Parser/` | `Ast.cs`（节点类型）、`TopLevelParser`、`StmtParser`、`ExprParser`（Pratt）、`TypeParser` |
| `TypeCheck/` | `TypeChecker` + 分部文件（Stmts / Exprs）、`Z42Type` 类型层次、`TypeEnv`（符号表）|
| `Codegen/` | `IrGen` + `IrGenStmts` + `IrGenExprs` |
| `Diagnostics/` | `DiagnosticBag`（收集器）+ `DiagnosticCatalog`（错误码目录）|
| `Features/` | `LanguageFeatures`（特性开关，按 Phase 分 profile）|

**设计约束**：不引入外部 parser combinator 库（为最终自举保留）；错误通过 `DiagnosticBag` 累积，用 `Z42Type.Error` 哨兵继续检查而非中止。

---

## z42.Project — 项目清单管理

**依赖**：z42.IR（使用其 `Z42Proj` / `ProjectMeta` 等类型）。

职责：读取 `.z42.toml`，向 Driver 提供可直接使用的构建配置。`z42.Compiler` 对此模块一无所知——编译器只接受源码字符串，不关心文件从哪里来。

| 文件 | 功能 |
|------|------|
| `ProjectManifest.cs` | 发现（`Discover`）、加载（`Load`）`.z42.toml`；解析 `[[exe]]` 多目标；解析 glob 模式得到源文件列表；解析 `pack` 优先级链（target → profile → project）|

**与 z42.IR/ProjectTypes.cs 的分工**：`ProjectTypes.cs` 是 TOML 的纯数据模型（反序列化目标类型），`ProjectManifest.cs` 是在此基础上的**发现与解析逻辑**（路径拼接、glob 展开、多目标路由）。

---

## z42.Driver — CLI 入口

**依赖**：z42.Compiler + z42.IR + z42.Project（三者全部）。

两个文件各有分工：

| 文件 | 职责 |
|------|------|
| `Program.cs` | 命令路由（当前手写 argv 解析）；单文件模式的完整 Lex → Parse → TypeCheck → Codegen → Emit 流程 |
| `BuildCommand.cs` | 项目模式的构建编排：加载清单 → 路由单目标/多目标 → 逐文件编译 → 组装 `.zpkg` |

`BuildCommand` 里的 `CompiledUnit` 是单文件编译结果的内部 DTO，只在 Driver 层使用。

---

## z42.Tests — 测试套件

覆盖编译器各阶段及关键数据格式：

| 文件 | 覆盖范围 |
|------|---------|
| `LexerTests.cs` | Token 识别、关键字、符号、数字/字符串字面量 |
| `ParserTests.cs` | AST 节点构造、组合子行为、错误恢复 |
| `TypeCheckerTests.cs` | 类型推断、运算符类型表、错误报告 |
| `IrGenTests.cs` | 字节码生成、寄存器分配、调用类型（static / virtual / builtin）|
| `GoldenTests.cs` | 端到端 pipeline：源文件 → 参考输出对比 |
| `GrammarSyncTests.cs` | 语法定义一致性校验 |
| `ProjectManifestTests.cs` | TOML 解析、多目标、glob 展开 |
| `ZbcRoundTripTests.cs` | `ZbcWriter` → `ZbcReader` 字节级往返验证 |

---

## 依赖关系图

```
z42.Tests ──────────────────────────────────────┐
                                                 ↓
z42.Driver ──→ z42.Compiler ──→ z42.IR ←── z42.Project
          └──→ z42.IR
          └──→ z42.Project
```

`z42.IR` 是唯一的无内部依赖层，所有其他模块均可安全引用它而不形成循环。
