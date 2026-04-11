# z42 语言编译器前端 — 工程架构设计

## 最终模块结构

```
z42/
├── z42.Core/           # 基础设施（无任何业务依赖）
├── z42.Syntax/         # 语法层
├── z42.Semantics/      # 语义层
├── z42.IR/             # 中间表示层
├── z42.Project/        # 项目解决方案和包管理
├── z42.Pipeline/       # 编译管线编排
├── z42.Driver/         # CLI 入口
└── z42.Tests/          # 测试套件
```

---

## 依赖关系总览

```
Driver
  └── Pipeline ──────────────────────────┐
        ├── Package                       │
        ├── Syntax ──→ Core              │
        ├── Semantics ──→ Syntax → Core  │
        ├── IR ──→ Semantics → Core      │
                                          │
              所有模块单向依赖 Core ←──────┘
              Core 不依赖任何模块
```

---

## 各模块目录结构 & 处理流程

---

### `z42.Core` — 基础设施

```
z42.Core/
├── Text/
│   ├── SourceText.cs        # 源码文本容器（内容 + 文件路径）
│   ├── TextSpan.cs          # 字节偏移范围 [start, end)
│   └── TextLocation.cs      # 行列号（用于显示）
├── Diagnostics/
│   ├── Diagnostic.cs        # 单条诊断（级别 + Span + 消息）
│   ├── DiagnosticBag.cs     # 诊断收集器
│   ├── DiagnosticSeverity.cs
│   └── DiagnosticCode.cs    # 错误码枚举
├── Collections/
│   ├── ImmutableArray.cs    # 轻量不可变集合
│   └── StringIntern.cs      # 字符串驻留池（标识符复用）
└── Utils/
    ├── Result.cs            # Result<T, E> 类型
    └── Option.cs            # Option<T> 类型
```

---

### `z42.Syntax` — 语法层

```
z42.Syntax/
├── Tokens/
│   ├── TokenKind.cs         # Token 类型枚举
│   ├── Token.cs             # Token（Kind + Text + Span）
│   └── TokenTrivia.cs       # 空白/注释附加信息
├── Lexer/
│   ├── Lexer.cs             # 主词法分析器
│   └── LexerState.cs        # 词法状态机
├── Ast/
│   ├── AstNode.cs           # 所有节点基类
│   ├── Expressions/         # 表达式节点
│   │   ├── BinaryExpr.cs
│   │   ├── CallExpr.cs
│   │   └── ...
│   ├── Statements/          # 语句节点
│   │   ├── LetStmt.cs
│   │   ├── ReturnStmt.cs
│   │   └── ...
│   └── Declarations/        # 声明节点
│       ├── FunctionDecl.cs
│       ├── StructDecl.cs
│       └── ...
├── Parser/
│   ├── Parser.cs            # 主语法分析器（递归下降）
│   ├── ParserContext.cs     # 解析上下文（Token流游标）
│   └── ParseRecovery.cs     # 错误恢复策略
└── SyntaxTree.cs            # 解析结果容器（AST根 + 诊断）
```

**处理流程：**
```
SourceText
    │
    ▼
  Lexer ──────────────────→ Token[]
    │  (字符流 → Token流)     │
    │                         ▼
    │                       Parser ──→ SyntaxTree
    │                 (Token流 → AST)      │
    │                                      ├── AstNode (根节点)
    │                                      └── Diagnostic[] (语法错误)
```

---

### `z42.Semantics` — 语义层

```
z42.Semantics/
├── Symbols/
│   ├── Symbol.cs            # 符号基类
│   ├── FunctionSymbol.cs
│   ├── VariableSymbol.cs
│   ├── TypeSymbol.cs
│   └── SymbolTable.cs       # 作用域符号表（树形结构）
├── Binder/
│   ├── Binder.cs            # 名称绑定器（AST遍历，填充符号表）
│   ├── Scope.cs             # 词法作用域
│   └── BinderContext.cs
├── Types/
│   ├── TypeDef.cs           # 类型定义基类
│   ├── PrimitiveType.cs     # int / float / bool / string
│   ├── FunctionType.cs
│   ├── StructType.cs
│   └── TypeInference.cs     # Hindley-Milner 类型推断
├── TypeChecker/
│   ├── TypeChecker.cs       # 类型检查器（遍历 BoundAst）
│   └── TypeConstraints.cs   # 类型约束收集与求解
├── Hir/
│   ├── HirNode.cs           # HIR 节点基类（携带类型信息）
│   ├── HirExpression.cs
│   ├── HirStatement.cs
│   └── HirDeclaration.cs
└── SemanticModel.cs         # 语义分析结果（HIR根 + 符号表 + 诊断）
```

**处理流程：**
```
SyntaxTree (AST)
    │
    ▼
  Binder ──────────────────→ SymbolTable
    │  (建立作用域，注册符号)      │
    │                             │
    ▼                             │
TypeChecker ◄────────────────────┘
    │  (类型推断 + 类型检查)
    ▼
  HIR ──────────────────────→ SemanticModel
    │  (携带类型信息的 AST)        │
                                  ├── HirRoot
                                  ├── SymbolTable
                                  └── Diagnostic[] (类型错误)
```

---

### `z42.IR` — 中间表示层

```
z42.IR/
├── Instructions/
│   ├── IrInstruction.cs     # 指令基类
│   ├── ArithmeticInstr.cs   # 算术指令
│   ├── ControlFlowInstr.cs  # 跳转/分支
│   ├── CallInstr.cs         # 函数调用
│   └── MemoryInstr.cs       # Load/Store
├── Structure/
│   ├── IrFunction.cs        # IR 函数（基本块列表）
│   ├── IrBasicBlock.cs      # 基本块
│   ├── IrModule.cs          # IR 模块（顶层容器）
│   └── IrValue.cs           # SSA 值
├── Builder/
│   ├── IrBuilder.cs         # HIR → IR Lowering
│   └── IrBuilderContext.cs
└── Passes/
    ├── IrPass.cs            # Pass 基类接口
    └── ConstantFolding.cs   # 常量折叠（可选优化）
```

**处理流程：**
```
SemanticModel (HIR)
    │
    ▼
  IrBuilder ───────────────→ IrModule
    │  (HIR → IR 指令序列)      │
    │                           ├── IrFunction[]
    │                           │     └── IrBasicBlock[]
    ▼                           │           └── IrInstruction[]
  Passes (可选)                 └── 全局符号信息
    │  (常量折叠 / 死代码消除)
    ▼
优化后的 IrModule
```

---


### `z42.Project` — 项目管理层

```
z42.Project/
├── Manifest/
│   ├── ProjectManifest.cs   # .z42.toml 数据模型
│   └── ManifestParser.cs    # TOML 解析
├── Resolver/
│   ├── DependencyResolver.cs # 依赖图解析
│   ├── VersionConstraint.cs  # SemVer 约束
└── Workspace/
    ├── WorkspaceLoader.cs    # 发现所有包
    └── ModuleGraph.cs        # 包间依赖 DAG
```

---

### `z42.Pipeline` — 编译管线

```
z42.Pipeline/
├── CompilationUnit.cs       # 单文件编译单元
├── PackageCompiler.cs       # 单包编译（协调 Syntax→Semantics→IR）
├── WorkspaceCompiler.cs     # 多包编译（按依赖拓扑序）
└── DiagnosticReporter.cs    # 统一诊断格式化输出（终端/JSON）
```

**整体端到端流程：**
```
.z42.toml
    │
    ▼
Package.Resolver ──→ ModuleGraph（包依赖拓扑序）
    │
    ▼（按拓扑序逐包编译）
Pipeline.PackageCompiler
    │
    ├─1─→ Syntax.Lexer ──→ Token[]
    ├─2─→ Syntax.Parser ──→ SyntaxTree
    ├─3─→ Semantics.Binder ──→ SymbolTable
    ├─4─→ Semantics.TypeChecker ──→ SemanticModel
    ├─5─→ IR.IrBuilder ──→ IrModule
```

---

### `z42.Driver` — CLI 入口

```
z42.Driver/
├── Program.cs               # 入口点
├── Commands/
│   ├── RunCommand.cs        # z42 run <file>
│   ├── CheckCommand.cs      # z42 check（只做类型检查）
│   ├── ReplCommand.cs       # z42 repl（交互模式）
│   └── PackageCommand.cs    # z42 add/remove/update
└── CliOptions.cs            # 命令行参数定义
```

---

### `z42.Tests` — 测试套件

```
z42.Tests/
├── Syntax.Tests/            # 词法 + 语法分析测试
├── Semantics.Tests/         # 类型检查 + 符号绑定测试
├── IR.Tests/                # IR 生成测试
└── Integration.Tests/       # 端到端集成测试
```

---

## 模块依赖矩阵

```
          Core  Syntax  Semantics  IR  Package  Pipeline
Core        ─
Syntax      ✓     ─
Semantics   ✓     ✓       ─
IR          ✓     ✗       ✓        ─
Package     ✓     ✗       ✗        ✗      ─
Pipeline    ✓     ✓       ✓        ✓      ✓        ─
Driver      ✗     ✗       ✗        ✗      ✗        ✓
```

✓ = 依赖，✗ = 不依赖，依赖严格单向，无循环。