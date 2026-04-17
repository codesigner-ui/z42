# z42 编译器架构评审报告

> 生成时间：2026-04-17  
> 分析范围：`src/compiler/` 全量代码  
> 参考基准：TypeScript Compiler、Roslyn、LLVM、Go toolchain、Cranelift

---

## 目录

1. [项目整体概览](#1-项目整体概览)
2. [各模块深度分析](#2-各模块深度分析)
3. [架构问题与技术债](#3-架构问题与技术债)
4. [数据驱动 / 配置驱动改进点](#4-数据驱动--配置驱动改进点)
5. [与成熟编译器的对比](#5-与成熟编译器的对比)
6. [改进路线图](#6-改进路线图)

---

## 1. 项目整体概览

### 1.1 目录结构

```
src/compiler/
├── z42.Core/
│   ├── Diagnostics/     DiagnosticBag, DiagnosticCodes, DiagnosticCatalog
│   ├── Features/        LanguageFeatures, LanguagePhase
│   └── Types/           Z42Type, Z42FuncType, Z42ClassType …
├── z42.Syntax/
│   ├── Lexer/           Lexer, TokenKind, TokenDefs
│   ├── Parser/          Parser, ExprParser, TypeParser
│   └── Ast/             CompilationUnit, Expr, Stmt, Decl …
├── z42.Semantics/
│   ├── TypeCheck/       TypeChecker (5 partial files)
│   │   ├── TypeChecker.cs
│   │   ├── TypeChecker.Classes.cs
│   │   ├── TypeChecker.Stmts.cs
│   │   ├── TypeChecker.Exprs.cs
│   │   └── TypeChecker.Analysis.cs
│   ├── BoundTree/       BoundExpr, BoundStmt (携带 Z42Type)
│   └── Codegen/
│       ├── IrGen.cs
│       ├── FunctionEmitter.cs          (4 partial files)
│       ├── FunctionEmitterExprs.cs
│       ├── FunctionEmitterStmts.cs
│       └── FunctionEmitterCalls.cs
├── z42.IR/
│   └── IrModule.cs      IrModule, IrFunction, IrBlock, IrInstr (47 种), IrTerminator
├── z42.Project/         ProjectManifest (TOML 项目配置)
├── z42.Pipeline/        编译流水线编排
└── z42.Tests/           GoldenTests 框架
```

### 1.2 数据流水线

```
源码文件
  │
  ▼ Lexer.cs (手写组合子词法器)
Token 流
  │
  ▼ Parser.cs + ExprParser.cs (Pratt + Superpower 风格)
AST (CompilationUnit / Expr / Stmt / Decl)
  │
  ▼ TypeChecker (两遍：收集形状 → 绑定体)
BoundTree + SemanticModel
  │
  ▼ IrGen + FunctionEmitter
IrModule (SSA-like, JSON 可序列化)
  │
  ▼ Rust Runtime (Interp / JIT / AOT)
执行结果
```

### 1.3 亮点设计

| 设计 | 位置 | 评价 |
|------|------|------|
| 声明式关键字/符号表 | `TokenDefs.cs` | ✅ 单点扩展，数据驱动 |
| Pratt 表达式解析器 | `ExprParser.cs` | ✅ 优先级管理清晰 |
| BoundTree 携带类型 | `BoundExpr/BoundStmt` | ✅ 代码生成无需查询类型环境 |
| 结构化错误码分区 | `DiagnosticCodes.cs` | ✅ 类 rustc 的错误体系 |
| `DiagnosticCatalog` explain | `DiagnosticCatalog.cs` | ✅ 可生成面向用户的错误说明 |
| Feature gate 字典 | `LanguageFeatures.cs` | ✅ 两行添加新特性 |
| Golden 测试自动发现 | `GoldenTests.cs` | ✅ 文件系统即测试用例 |
| TOML 项目清单 | `ProjectManifest.cs` | ✅ 配置驱动项目结构 |

---

## 2. 各模块深度分析

### 2.1 词法器（Lexer / TokenDefs）

**核心机制**：4 段分发（字符串 → 数字 → 标识符/关键字 → 符号），符号按首字符分组为 `SymbolIndex` 实现 O(1) 前缀查找，支持最长匹配。

**`TokenDefs.cs` 关键结构**：
```csharp
// 关键字映射：解析器唯一真理来源
IReadOnlyDictionary<string, TokenKind> Keywords = new Dictionary<string, TokenKind> {
    { "return", TokenKind.Return },
    { "fn",     TokenKind.Fn },      // Phase 2 保留
    { "let",    TokenKind.Let },     // Phase 2 保留
    // ...
};

// 符号规则：2 字符优先于 1 字符（最长匹配）
record SymbolRule(string Text, TokenKind Kind);
List<SymbolRule> SymbolRules = [ new("==", Eq), new("=", Assign), ... ];
```

**现有问题**：
- Phase 1 和 Phase 2 保留关键字混在同一字典，没有阶段标注；词法器无法感知当前阶段
- `NumericSuffixes`（`L,u,f,d,m`）是裸字符串集合，和类型系统之间的映射散落在 `Lexer.cs` 逻辑中

---

### 2.2 语法分析器（Parser / ExprParser）

**Pratt 解析器核心**（`ExprParser.cs`）：

```csharp
// NudEntry / LedEntry 带特性门控
record NudEntry(NudFn Fn, string? RequiredFeature);
record LedEntry(int Bp, LedFn Fn, string? RequiredFeature);

// Led 表（优先级硬编码为魔法数字）
s_ledTable = new Dictionary<TokenKind, LedEntry> {
    { TokenKind.Assign,   new(10, AssignLed,   null) },
    { TokenKind.Or,       new(30, BinaryLeft,  null) },
    { TokenKind.BitAnd,   new(44, BinaryLeft, "bitwise") },  // 特性门控
    { TokenKind.Star,     new(80, BinaryLeft,  null) },
    // ...
};
```

**现有问题**：

| 位置 | 问题 |
|------|------|
| `ExprParser.cs:40-41` | 特性禁用时抛 `ParseException` 而非返回 `Fail`，与其他错误路径不一致 |
| `ExprParser.cs:370-371` | Cast 检测依赖 `Peek(1)` 但不验证索引有效，边界情况脆弱 |
| 全表 | 优先级为魔法数字（10/30/44/80/90…），无命名常量 |

---

### 2.3 类型检查器（TypeChecker）

**两遍设计**：
```
Pass 0a: CollectEnums
Pass 0b: CollectInterfaces
Pass 0c: CollectClasses (→ Classes.cs)
Pass 0d: CollectFunctions
Pass 0e: BindStaticFieldInits
Pass 1:  BindClassMethods / BindFunction (→ Stmts.cs / Exprs.cs)
分析:    CheckDefiniteAssignment / ReturnAnalysis (→ Analysis.cs)
```

**私有状态字段**（`TypeChecker.cs:27-55`，共约 15 个字段）：
```csharp
readonly DiagnosticBag _diags;
readonly Dictionary<string, Z42FuncType>     _funcs;
readonly Dictionary<string, Z42ClassType>    _classes;
readonly Dictionary<string, Z42InterfaceType>_interfaces;
readonly Dictionary<FunctionDecl, BoundBlock> _boundBodies;
readonly Dictionary<Param, BoundExpr>         _boundDefaults;
readonly Dictionary<FieldDecl, BoundExpr>     _boundStaticInits;
// ... 还有 _classInterfaces, _abstractMethods, _virtualMethods …
string? _currentClass;  // 可变全局上下文
```

**现有问题**：
- `_currentClass` 是可变全局上下文，partial 文件之间隐式共享，重入不安全
- `TypeChecker.cs:127`：重复函数声明使用 `TypeMismatch`（E0402）代码，语义不正确，应为 `DuplicateDeclaration`
- 5 个 partial 文件约 1000+ 行，但职责边界仅靠文件名约定，没有接口隔离
- `BindClassMethods` 依赖执行顺序（基类必须先于派生类处理），但没有拓扑排序保证

---

### 2.4 IR 生成（IrGen / FunctionEmitter）

**`IrGen.cs` 生成流程**：
```csharp
Generate(CompilationUnit cu):
  1. 验证 SemanticModel 非空
  2. 从 SemanticModel 填充 7 个派生字典（避免重复遍历 AST）
  3. 发出 IrClassDesc 列表
  4. 发出 __static_init__ 函数
  5. 按类发出方法 → FunctionEmitter
  6. 发出顶级函数 → FunctionEmitter
  7. 返回 IrModule
```

**类型映射重复问题**（3 处相同逻辑）：
```csharp
// IrGen.cs:243
string TypeName(TypeExpr t) { ... }

// FunctionEmitter.cs:173
string TypeName(TypeExpr t) { ... }   // ← 重复

// FunctionEmitter.cs:196-212
static readonly Dictionary<string, IrType> IrTypeByName = new() {
    { "int", IrType.I32 }, { "long", IrType.I64 }, ...
};
```

**`FunctionEmitter` 状态字段**（`FunctionEmitter.cs:22-34`）：
```csharp
int _nextReg;                          // 寄存器分配计数
int _nextLabelId;                      // 标签 ID
Dictionary<string, TypedReg> _locals;  // 局部变量
HashSet<string> _mutableVars;          // var 声明变量
HashSet<string> _instanceFields;       // 实例字段名
List<IrBlock> _blocks;                 // 生成块列表
List<IrExceptionEntry> _exceptionTable;
Stack<(string Break, string Continue)> _loopStack;
string? _currentClassName;
```

**现有问题**：

| 位置 | 问题 |
|------|------|
| `FunctionEmitter.cs:47` | `this` 寄存器硬编码为 `IrType.Ref`，注释写明"无类型检查" |
| `IrGen.cs:90-92` | 重载检测 `ct != null` 判断可能遗漏错误 |
| `IrGen.cs:160-162` | BoundBody 缺失触发运行时异常，应在类型检查期捕获 |
| `FunctionEmitter.cs:186-190` | `WriteBackName` 三路分支（局部/可变/字段）逻辑复杂，排他性未验证 |
| 静态初始化 | 按 CU 顺序处理，无拓扑排序；静态字段互相依赖时可能出错 |

---
### 2.5 IR 模块（IrModule）

**指令体系（47 种）**，通过 `[JsonPolymorphic]` + `[JsonDerivedType]` 注册：

```
常量:   ConstStr, ConstI32, ConstI64, ConstF64, ConstBool, ConstChar, ConstNull
数据:   Copy, StrConcat, ToStr
调用:   Call, Builtin
算术:   Add, Sub, Mul, Div, Rem
比较:   Eq, Ne, Lt, Le, Gt, Ge
逻辑:   And, Or, Not, Neg
位操作: BitAnd, BitOr, BitXor, BitNot, Shl, Shr
变量:   Store, Load
数组:   ArrayNew, ArrayNewLit, ArrayGet, ArraySet, ArrayLen
对象:   ObjNew, FieldGet, FieldSet, VCall, IsInstance, AsCast, StaticGet, StaticSet
```

**终止符（4 种）**：
```csharp
record RetTerm(TypedReg? Reg)
record BrTerm(string Label)
record BrCondTerm(TypedReg Cond, string TrueLabel, string FalseLabel)
record ThrowTerm(TypedReg Reg)
```

**现有问题**：
- `IrModule.cs:127`：47 个类型通过手工 `[JsonDerivedType]` 属性注册，新增指令需同时修改 3 处（类定义、属性列表、`FunctionEmitter` switch）
- `IrFunction.MaxReg = 0` 表示未知，VM 动态调整；缺少编译器侧的寄存器压缩
- 没有 IR well-formedness 验证（每个 use 有 def，每个 block 有唯一 terminator）

---

### 2.6 特性门控（LanguageFeatures）

**当前设计**：
```csharp
public sealed class LanguageFeatures {
    IReadOnlyDictionary<string, bool> _flags;

    // Minimal preset — 仅 Hello World
    public static LanguageFeatures Minimal = new(new Dictionary<string, bool> {
        ["interpolated_str"] = true,
        ["control_flow"]     = false,
        // ...
    });

    // 未知特性默认 true（前向兼容）
    public bool IsEnabled(string key) =>
        _flags.TryGetValue(key, out var v) ? v : true;  // ← 危险默认值
}
```

**现有问题**：
- 未知特性键默认 `true`：`"controle_flow"` 拼写错误会被静默启用
- `KnownFeatureNames` 绑定到 `Phase1._flags.Keys`，Phase 2 新特性不会自动同步
- 特性之间没有依赖关系声明（如 `nullable` 依赖 `oop`）
- 没有 grammar 注释（`[feat:NAME]`）与此列表的一致性校验

---

### 2.7 诊断系统（Diagnostics）

**分区设计**（优点）：
```
E01xx — Lexer        E02xx — Parser
E03xx — Feature      E04xx — TypeChecker
E05xx — IrGen        E09xx — Native
```

**现有问题**：
- `E0402 TypeMismatch` 涵盖 10+ 种情况（类型不匹配、void 赋值、非 bool 条件、重复函数等），诊断太泛化
- 缺少 Warning 级别的诊断使用（目前 Warning 枚举存在但代码中未见使用）
- `E05xx` 之后没有规划（测试覆盖到 E0501），Phase 2/3 扩展空间不明确

---

## 3. 架构问题与技术债

### 3.1 🔴 缺少访问者/变换接口

**问题**：所有 AST/BoundTree 变换（TypeChecker、IrGen、FunctionEmitter）均使用 `switch` on 节点类型，分散在多个文件。新增 AST 节点需修改 N 处 switch，编译器无法发出遗漏警告。

**对比**：Roslyn 的 `CSharpSyntaxVisitor<TResult>`；LLVM 的 `InstVisitor<SubClass, RetTy>`；Go 的 `ast.Inspect`。

**改进方案**：
```csharp
// 在 AST/BoundExpr 上添加 Accept
abstract record BoundExpr {
    public abstract TResult Accept<TResult>(IBoundExprVisitor<TResult> v);
}

// 每个 pass 实现 visitor
interface IBoundExprVisitor<TResult> {
    TResult VisitLiteral(BoundLiteral node);
    TResult VisitBinary(BoundBinary node);
    TResult VisitCall(BoundCall node);
    // 新增节点 → 编译器强制实现者补全
}

// TypeChecker, IrGen, FunctionEmitter 各实现一个 Visitor
class ExprEmitter : IBoundExprVisitor<TypedReg> { ... }
```

---

### 3.2 🔴 TypeChecker 职责未隔离

**问题**：5 个 partial 文件共享 ~15 个私有字段，没有接口边界，重构任意一个 pass 都会触及其他。`_currentClass` 可变全局状态在并发场景不安全。

**改进方案**：拆分为独立阶段，通过数据结构传递：
```
ISymbolBinder      → CollectEnums / CollectInterfaces / CollectClasses
                     输出: SymbolTable
ITypeInferrer      → BindBodies（含 Exprs/Stmts）
                     输入: SymbolTable
                     输出: BoundTree + TypeAnnotations
IFlowAnalyzer      → CheckDefiniteAssignment / ReturnAnalysis
                     输入: BoundTree
                     输出: Diagnostics
```

---

### 3.3 🔴 缺少优化 Pass 层

**问题**：从 BoundTree 直接降级到 IR，没有任何中间优化层。对于多执行模式（Interp/JIT/AOT），各自优化策略不同，目前无法差异化。

**改进方案**：引入 `PassManager`：
```csharp
interface IIrPass {
    string Name { get; }
    IrModule Run(IrModule input, PassContext ctx);
}

class PassManager {
    readonly List<IIrPass> _pipeline = new();

    public PassManager Add(IIrPass pass) { _pipeline.Add(pass); return this; }
    public IrModule RunAll(IrModule m, PassContext ctx) =>
        _pipeline.Aggregate(m, (acc, p) => p.Run(acc, ctx));
}

// 按执行模式配置不同 pipeline
var interpPipeline = new PassManager()
    .Add(new ConstantFoldingPass())
    .Add(new DeadCodeEliminationPass());

var jitPipeline = interpPipeline
    .Add(new InliningPass())
    .Add(new RegisterCoalescingPass());
```

---

### 3.4 🟠 类型映射代码重复（DRY 违反）

**问题**：`TypeName(TypeExpr)` 和 `IrTypeByName` 字典在 `IrGen.cs` 和 `FunctionEmitter.cs` 中各有实现，3 处逻辑重复。

**改进方案**：提取为 `IrTypeMapping` 静态工具类：
```csharp
internal static class IrTypeMapping {
    public static string ToName(TypeExpr t) { ... }
    public static IrType ToIrType(Z42Type t) { ... }
    public static IrType ToIrType(TypeExpr t) => ToIrType(Z42Type.Resolve(t));
}
```

---

### 3.5 🟠 静态初始化无拓扑排序

**问题**：`FunctionEmitter.EmitStaticInit` 按 CU 中类的声明顺序处理静态字段，若 A 的静态初始化器引用 B 的静态字段，而 B 声明在 A 之后，则运行时读到未初始化值。

**改进方案**：在 IrGen 阶段构建静态字段依赖图，拓扑排序后再发射 `__static_init__`。

---

### 3.6 🟡 错误恢复策略缺失

**问题**：Parser 出错后没有明确的同步点（synchronization token）策略，大型错误会导致级联假阳性诊断。

**改进方案**：
```csharp
// 定义同步点集合
static readonly ImmutableHashSet<TokenKind> SyncTokens = [
    TokenKind.Semicolon, TokenKind.RBrace, TokenKind.Fn, TokenKind.Class
];

// 出错后跳到最近同步点
ParseResult<T> RecoverTo(TokenCursor cursor, ImmutableHashSet<TokenKind> sync) {
    while (!cursor.IsEnd && !sync.Contains(cursor.Current.Kind))
        cursor = cursor.Advance();
    return ParseResult.Fail(...);
}
```

---

### 3.7 🟡 IR well-formedness 缺少验证

**问题**：IR 发射后直接送往 Rust 运行时，没有 IR 合法性校验；调试时若发射出非法 IR（use-before-def、block 无 terminator）只能在运行时崩溃。

**改进方案**：添加 `IrVerifier` 在 Debug 构建时运行：
```csharp
static class IrVerifier {
    public static IReadOnlyList<string> Verify(IrModule m) {
        var errors = new List<string>();
        foreach (var fn in m.Functions) {
            VerifyDefUse(fn, errors);
            VerifyTerminators(fn, errors);
            VerifyExceptionTable(fn, errors);
        }
        return errors;
    }
}
```

---

## 4. 数据驱动 / 配置驱动改进点

| 现状 | 问题 | 推荐方案 |
|------|------|----------|
| `ExprParser` 优先级魔法数字（10/30/44/80…） | 修改优先级需要读懂相对关系 | 提取 `Precedence` 命名常量枚举；或统一在 `OperatorDefs` 数据表 |
| `LanguageFeatures` 字符串键，未知默认 `true` | 拼写错误静默通过 | 改为枚举 + `[FeatureGate]` Attribute，编译期校验 |
| `TokenDefs.Keywords` Phase 1/2 混合 | 无阶段隔离 | 每个关键字标注 `Phase` 字段，词法器按当前阶段过滤 |
| `[JsonDerivedType]` 手工注册 47 条指令 | 新增指令需改 3 处 | Source Generator 自动扫描 `IrInstr` 子类生成注册代码 |
| `DiagnosticCatalog` 手工维护文档 | 与代码容易不同步 | `[DiagnosticDoc]` Attribute + Source Generator 生成 Catalog |
| Golden 测试 `features.toml` 覆盖 | 已较好 | 增加测试矩阵：每个 feature 组合自动生成测试变体 |
| 无优化 pass 配置 | 调试/发布无差异 | `PassManager` 按 profile 配置 pass 列表（见 §3.3）|

### 4.1 算符优先级数据化（详细方案）

```csharp
// OperatorDefs.cs — 单一数据源
internal static class OperatorDefs {
    internal record BinaryOpDef(
        TokenKind Token,
        int Bp,
        Assoc Assoc,
        string? RequiredFeature = null,
        string IrOpName = ""          // 对应 IR 指令名
    );

    internal static readonly BinaryOpDef[] All = [
        new(TokenKind.Assign,  Bp: 10, Assoc.Right),
        new(TokenKind.Or,      Bp: 30, Assoc.Left,  IrOpName: "or"),
        new(TokenKind.And,     Bp: 40, Assoc.Left,  IrOpName: "and"),
        new(TokenKind.BitAnd,  Bp: 44, Assoc.Left,  "bitwise", "bit_and"),
        new(TokenKind.BitOr,   Bp: 46, Assoc.Left,  "bitwise", "bit_or"),
        new(TokenKind.Eq,      Bp: 50, Assoc.Left,  IrOpName: "eq"),
        new(TokenKind.Star,    Bp: 80, Assoc.Left,  IrOpName: "mul"),
        // ...
    ];
}
```

这样 `ExprParser`、`TypeChecker`（运算符类型检查）、文档生成器均从同一数据源驱动。

### 4.2 特性门控枚举化（详细方案）

```csharp
// 从字符串键改为枚举
public enum LanguageFeature {
    [FeatureGate("interpolated_str", Since = LanguagePhase.Phase1)]
    InterpolatedStr,

    [FeatureGate("nullable", Since = LanguagePhase.Phase1, DependsOn = [Oop])]
    Nullable,

    [FeatureGate("trait_objects", Since = LanguagePhase.Phase2, DependsOn = [TypeClasses])]
    TraitObjects,
}

// 编译期检查依赖，自动生成文档
public class LanguageFeatures {
    public bool IsEnabled(LanguageFeature f) =>
        _flags.TryGetValue(f, out var v) && v;   // 未知 → false，安全默认
}
```

---

## 5. 与成熟编译器的对比

### 5.1 可维护性评分（10 分满分）

| 维度 | z42 现状 | TypeScript | Roslyn | Go toolchain | 改进空间 |
|------|---------|------------|--------|--------------|---------|
| 词法器可扩展性 | 8 | 7 | 9 | 7 | 加阶段标注 → 9 |
| 算符优先级管理 | 5 | 8 | 9 | 7 | 数据化 → 8 |
| 类型检查器结构 | 5 | 7 | 9 | 8 | 职责分离 → 8 |
| 代码生成可扩展性 | 5 | 7 | 8 | 8 | Visitor → 8 |
| 优化层 | 2 | 5 | 4 | 6 | PassManager → 6 |
| 错误诊断 | 7 | 8 | 9 | 9 | 细化 TypeMismatch → 9 |
| 测试基础设施 | 6 | 8 | 9 | 9 | 属性测试 → 8 |
| 配置驱动程度 | 6 | 7 | 8 | 6 | 枚举化 → 8 |

### 5.2 关键设计对比

**TypeScript Compiler**
- `binder.ts`（名字绑定）、`checker.ts`（类型推断）、`emitter.ts`（代码生成）完全隔离
- `SyntaxVisitor` 模式，新增节点必须实现所有 visitor
- z42 的 TypeChecker 5 partial = TS 的 checker.ts 一个文件 5 万行（同样的问题，不同的表现形式）

**Roslyn**
- `SyntaxNode.Accept(visitor)` 访问者模式贯穿全流程
- Red/Green Tree（不可变语法树 + 可变包装）支持增量编译
- z42 AST 使用 sealed records（不可变 ✅），但缺少 Accept

**LLVM**
- `PassManager` + `AnalysisManager`：pass 之间通过分析结果显式依赖，可缓存
- `InstVisitor<T>` 模板：新增指令 → 编译期报错未实现的 visitor
- z42 IR 47 条指令完全靠运行时 switch 分发

**Cranelift（Rust JIT）**
- 数据流图（CLIF）+ 可插拔后端
- 指令定义在 `.isle` 规则文件（数据驱动），编译器生成 Rust 代码
- z42 可考虑类似的指令定义 DSL

**Go toolchain**
- `ast.Walk` / `ast.Inspect` 统一树遍历
- 静态初始化依赖通过 init graph 排序（z42 缺失）
- 诊断分级（error/warning/note）z42 已有类似结构但 warning 未使用

---
## 6. 改进路线图

### Phase A（基础设施，不改变语言语义）

| 优先级 | 任务 | 收益 | 工作量 |
|--------|------|------|--------|
| P0 | 提取 `IrTypeMapping` 工具类，消除 3 处重复 | 消除 DRY 违反 | S |
| P0 | `OperatorDefs` 数据表替代魔法数字 | 可读性 + 可扩展 | S |
| P0 | `LanguageFeature` 枚举化，未知默认 false | 安全性 | M |
| P1 | 添加 `IBoundExprVisitor` / `IBoundStmtVisitor` | 新增节点编译期保护 | M |
| P1 | `IrVerifier`（Debug 构建时自动运行） | 提前暴露 bug | S |
| P1 | 细化 E0402，拆分 `DuplicateDeclaration`、`VoidAssignment` 等 | 错误可读性 | S |
| P2 | `PassManager` 框架（初期 0 个 pass） | 为优化奠基 | M |
| P2 | 静态初始化拓扑排序 | 正确性 | M |

### Phase B（TypeChecker 重构）

| 优先级 | 任务 | 收益 | 工作量 |
|--------|------|------|--------|
| P1 | 提取 `ISymbolBinder` 接口，隔离 Pass 0 | 独立测试 | L |
| P1 | `_currentClass` 改为 `CheckContext` 值对象传参 | 线程安全 + 可测 | M |
| P2 | 提取 `IFlowAnalyzer` 接口（DefAssign/Return 分析） | 独立 pass | L |

### Phase C（测试与工具链）

| 优先级 | 任务 | 收益 | 工作量 |
|--------|------|------|--------|
| P1 | Lexer round-trip 属性测试（FsCheck） | 发现边界 bug | S |
| P1 | Parser round-trip 属性测试（parse→print→parse）| 一致性保证 | M |
| P2 | IR well-formedness 属性测试 | 生成器正确性 | M |
| P2 | Feature 矩阵测试（每个 feature 开关的 golden 用例）| 特性隔离验证 | L |

### Phase D（Phase 2 语言特性前置准备）

在开始实现 trait/泛型/闭包之前，以下工作必须完成：
1. ✅ Visitor 接口（否则每个新 AST 节点 = N 处 switch 修改）
2. ✅ TypeChecker 职责分离（泛型约束求解需要独立 `ConstraintSolver`）
3. ✅ PassManager（闭包/内联需要 IR 级变换）
4. ✅ LanguageFeature 枚举 + 依赖声明（Phase 2 特性之间依赖复杂）

---

## 附录：快速参考

### 需要立即修复的 Bug 风险点

```
IrGen.cs:90-92     — 重载检测可能遗漏，ct==null 时静默跳过
IrGen.cs:160-162   — BoundBody 缺失运行时异常，应提前检查
TypeChecker.cs:127 — DuplicateDeclaration 错误使用 TypeMismatch 代码
FunctionEmitter.cs:47 — this 寄存器无类型信息，假设 Ref
LanguageFeatures.cs:92 — 未知特性默认 true（拼写错误静默通过）
```

### 数据流依赖图（当前）

```
TokenDefs ─────────────────────► Lexer
LanguageFeatures ──┬────────────► ExprParser (NudTable/LedTable)
                   └────────────► TypeChecker
                   └────────────► IrGen
Z42Type.PrimTable ──────────────► TypeChecker
BinaryTypeTable ────────────────► TypeChecker.Exprs
SemanticModel ──────────────────► IrGen → FunctionEmitter
StdlibCallIndex ────────────────► IrGen
```

### 代码规模统计

| 模块 | 主要文件 | 估计行数 | 复杂度 |
|------|---------|---------|--------|
| Lexer/TokenDefs | 3 文件 | ~400 | 低 |
| Parser/ExprParser | 4 文件 | ~600 | 中 |
| TypeChecker | 5 partial | ~1200 | 高 |
| IrGen+FunctionEmitter | 5 partial | ~1500 | 高 |
| IR定义 | 1 文件 | ~300 | 低 |
| Diagnostics | 3 文件 | ~300 | 低 |

---

*报告结束。建议优先推进 Phase A 的 P0/P1 项，这些改动风险低、收益即时，且为后续 Phase 2 语言特性扩展铺路。*

---

## 7. 补充分析：首轮评审摘要

> 本节保留首次架构评审中的关键结论，作为本文档的补充视角。

### 7.1 总体架构演进建议

z42 的整体编译流程当前为：

```
当前：  Lexer → Parser → TypeChecker(单体) → IrGen → Runtime

建议：  Lexer → Parser → Binder → TypeInferrer → LoweringPass
                                                  → OptPassManager（可插拔）
                                                  → IrGen → Runtime
```

其中 `LoweringPass` 负责将高级 BoundTree 构造（如 foreach、using、string interpolation）展开为低级等价形式，再交给 IrGen 处理纯结构化降级，职责更单一。

---

### 7.2 问题补充：错误恢复策略不明确

**现状**：`DiagnosticBag` 使用 sentinel 类型（`Z42Type.Error`、`Z42Type.Unknown`）继续解析，但没有明确的 **panic-mode 恢复** 或 **synchronization point** 规范。一处大型错误会导致级联假阳性诊断，用户面对大量无意义错误。

**参考**：
- Rust compiler 的 `Recover` trait：遇到错误时尝试恢复并继续解析
- TypeScript 的 `errorRecovery`：跳过无法解析的令牌，继续到下一个语句边界

**改进方向**：
- **Parser 层**：引入 synchronization token 集合（`;`、`}`、`fn`、`class`），出错后跳到最近同步点，避免无限错误扩散
- **TypeChecker 层**：每个函数独立隔离错误，避免一个函数的类型错误污染其他函数的推断结果

```csharp
// 示例：函数级错误隔离
BoundBlock? TryBindFunction(FunctionDecl fn) {
    using var scope = _diags.BeginIsolatedScope();
    try {
        return BindBody(fn);
    } catch (FatalBindException) {
        scope.MarkFailed();
        return null;  // 其他函数继续正常检查
    }
}
```

---

### 7.3 问题补充：测试覆盖不足（缺乏属性测试与 Fuzzing）

**现状**：仅有 Golden Tests（输入输出对比），缺少属性测试（Property-Based Testing）和模糊测试（Fuzzing）。随着语言特性增加，手写 golden 用例覆盖组合爆炸。

**改进方向**：

#### Lexer Round-Trip 属性测试
```csharp
// 性质：tokenize → detokenize → tokenize 结果一致
[Property]
bool LexerRoundTrip(string source) {
    var tokens1 = Lexer.Tokenize(source);
    var reconstructed = string.Join("", tokens1.Select(t => t.Text));
    var tokens2 = Lexer.Tokenize(reconstructed);
    return tokens1.SequenceEqual(tokens2);
}
```

#### Parser Round-Trip 属性测试
```csharp
// 性质：parse → pretty-print → parse 产生等价 AST
[Property]
bool ParserRoundTrip(ValidZ42Program program) {
    var ast1 = Parser.Parse(program.Source);
    var printed = PrettyPrinter.Print(ast1);
    var ast2 = Parser.Parse(printed);
    return AstEquals(ast1, ast2);
}
```

#### IR Well-formedness 断言
```csharp
// 每次 FunctionEmitter 完成后自动验证（Debug 构建）
#if DEBUG
IrVerifier.Verify(fn); // 验证：每个 use 有 def、每个 block 有唯一 terminator
#endif
```

#### Feature 矩阵测试
对每个 `LanguageFeature` 枚举值，自动生成启用/禁用该特性的测试变体，确保特性门控真正隔离行为。

---

### 7.4 各模块数据驱动程度汇总

| 模块 | 当前方式 | 推荐方式 | 优先级 |
|------|---------|---------|--------|
| 算符优先级（`ExprParser`） | 硬编码魔法数字 | `OperatorDefs` 数据表 | P0 |
| 特性门控（`LanguageFeatures`） | 字符串键，未知默认 true | 枚举 + `[FeatureGate]` Attribute | P0 |
| 关键字阶段隔离（`TokenDefs`） | Phase 1/2 混合字典 | 每条记录标注 `Phase` 字段 | P1 |
| IR 指令注册（`IrModule`） | 47 条手工 `[JsonDerivedType]` | Source Generator 自动扫描子类 | P1 |
| 诊断文档（`DiagnosticCatalog`） | 手工维护 | `[DiagnosticDoc]` Attribute + Generator | P2 |
| 优化 pass 配置 | 不存在 | `PassManager` 按 profile 配置 | P2 |
| 类型映射（IrGen/FunctionEmitter） | 3 处重复 | 统一 `IrTypeMapping` 工具类 | P0 |

---

### 7.5 Phase 2 语言特性扩展的前置条件

z42 计划在 Phase 2 引入 trait/泛型/闭包/所有权等特性。在此之前，以下架构债务**必须**提前偿还，否则将在实现阶段造成数倍返工：

| 前置条件 | 原因 | 若不解决的后果 |
|---------|------|--------------|
| Visitor 接口 | 泛型实例化会产生大量新 AST 节点 | 每个新节点需改 N 处 switch |
| TypeChecker 职责分离 | 泛型约束求解需要独立的 `ConstraintSolver` pass | 无法干净地插入新 pass |
| PassManager 框架 | 闭包捕获分析、内联优化需要 IR 级变换 | 所有优化硬编码在 IrGen 里 |
| LanguageFeature 枚举 + 依赖声明 | Phase 2 特性之间依赖复杂（如 `trait_objects` 依赖 `type_classes`） | 特性组合行为不可预测 |
| 静态初始化拓扑排序 | 泛型静态字段的初始化顺序更复杂 | 运行时初始化顺序错误 |

---

### 7.6 综合优先级矩阵

综合风险、工作量、收益三个维度：

```
优先级  任务                              风险  工时  收益
──────────────────────────────────────────────────────────
[P0]   提取 IrTypeMapping，消除重复        低    S    立即
[P0]   OperatorDefs 数据表               低    S    立即
[P0]   LanguageFeature 枚举化             低    M    安全
[P1]   IBoundExprVisitor 接口             中    M    架构
[P1]   IrVerifier（Debug 断言）           低    S    质量
[P1]   细化 E0402 诊断码                  低    S    体验
[P1]   Lexer/Parser 属性测试              低    M    质量
[P2]   PassManager 框架（0 个 pass）      低    M    扩展
[P2]   静态初始化拓扑排序                  中    M    正确
[P2]   TypeChecker 职责分离               高    L    架构
[P3]   Parser 错误恢复策略                中    L    体验
[P3]   IR 属性测试 + Fuzzing              低    L    质量
```

> **S** = 小（<1天）  **M** = 中（1-3天）  **L** = 大（>3天）

---

*完整报告结束。*

---

## 8. 七大不足汇总（执行摘要）

> 本章将全文散落的核心不足集中呈现，便于快速决策和任务分配。

---

### 不足 1 🔴 缺少访问者 / 变换接口

**问题描述**：所有 AST 与 BoundTree 的变换（TypeChecker、IrGen、FunctionEmitter）均使用裸 `switch` 语句对节点类型进行模式匹配，逻辑散落在多个文件。每当新增一种 AST 节点，需要手动找到并修改 N 处 `switch`，编译器本身无法发出"遗漏处理"的警告。

**影响范围**：`TypeChecker.Exprs.cs`、`FunctionEmitterExprs.cs`、`IrGen.cs` 等所有遍历 BoundTree 的文件。

**建议改进**：
```csharp
// BoundExpr 基类增加 Accept
abstract record BoundExpr {
    public abstract TResult Accept<TResult>(IBoundExprVisitor<TResult> v);
}

// 统一 Visitor 接口 — 新增节点时编译器强制补全所有实现
interface IBoundExprVisitor<TResult> {
    TResult VisitLiteral(BoundLiteral node);
    TResult VisitBinary(BoundBinary node);
    TResult VisitCall(BoundCall node);
    TResult VisitAssign(BoundAssign node);
    // ...
}
```

**参考**：Roslyn `CSharpSyntaxVisitor<TResult>`；LLVM `InstVisitor<T>`；Go `ast.Inspect`。

---

### 不足 2 🔴 TypeChecker 职责未隔离（单体巨类）

**问题描述**：`TypeChecker` 被拆为 5 个 partial 文件（cs / Classes.cs / Stmts.cs / Exprs.cs / Analysis.cs），但本质上是一个拥有约 15 个共享私有字段的单体类。其中 `_currentClass` 是可变全局状态，partial 文件之间隐式共享，重构任意一个 pass 都会触及其他，且在并发场景不安全。Pass 之间的依赖靠执行顺序约定，没有接口边界保证。

**影响范围**：`z42.Semantics/TypeCheck/` 全部 5 个文件。

**建议改进**：拆分为职责独立的阶段，通过显式数据结构传递：
```
ISymbolBinder   → Pass 0（收集枚举/接口/类/函数签名）  输出: SymbolTable
ITypeInferrer   → Pass 1（绑定表达式/语句体）          输入: SymbolTable → 输出: BoundTree
IFlowAnalyzer   → Pass 2（确定赋值/返回路径分析）       输入: BoundTree  → 输出: Diagnostics
```
`_currentClass` 改为 `CheckContext` 值对象，随调用栈显式传递，消除可变全局状态。

**参考**：TypeScript Compiler（`binder.ts` + `checker.ts` 严格分离）；Roslyn（`Binder` 层次结构）。

---

### 不足 3 🔴 缺少优化 Pass 层

**问题描述**：从 BoundTree 直接降级到 IR（direct lowering），没有任何中间优化层。z42 支持 Interp / JIT / AOT 三种执行模式，各自对优化的需求不同，但目前无法差异化配置；所有优化逻辑若要添加只能硬塞进 `IrGen`，进一步膨胀已经复杂的生成器。

**影响范围**：`z42.Semantics/Codegen/IrGen.cs`；未来所有需要 IR 级变换的特性（闭包、内联、泛型单态化）。

**建议改进**：引入轻量 `PassManager`，初期哪怕 0 个 pass，框架建立后可持续叠加：
```csharp
interface IIrPass {
    string Name { get; }
    IrModule Run(IrModule input, PassContext ctx);
}

class PassManager {
    public PassManager Add(IIrPass pass) { ... }
    public IrModule RunAll(IrModule m, PassContext ctx) =>
        _pipeline.Aggregate(m, (acc, p) => p.Run(acc, ctx));
}

// 按执行模式配置不同 pipeline
var interpPipeline = new PassManager()
    .Add(new ConstantFoldingPass())
    .Add(new DeadCodeEliminationPass());
```

**参考**：LLVM `PassManager` + `AnalysisManager`；Cranelift 可插拔后端 pass 体系。

---

### 不足 4 🟠 类型映射代码重复（DRY 违反）

**问题描述**：`TypeName(TypeExpr t)` 方法和 `IrTypeByName` 映射字典在 `IrGen.cs`（行 243）与 `FunctionEmitter.cs`（行 173、行 196-212）中各有一份实现，共 3 处重复相同逻辑。任何类型新增或重命名需要同步修改 3 处，容易遗漏导致不一致。

**影响范围**：`IrGen.cs:243`、`FunctionEmitter.cs:173`、`FunctionEmitter.cs:196-212`。

**建议改进**：提取为统一工具类：
```csharp
internal static class IrTypeMapping {
    public static string ToName(TypeExpr t) { ... }
    public static IrType ToIrType(Z42Type t) { ... }
    public static IrType ToIrType(TypeExpr t) => ToIrType(Z42Type.Resolve(t));
}
```

---

### 不足 5 🟠 静态初始化无拓扑排序

**问题描述**：`FunctionEmitter.EmitStaticInit` 按类在 `CompilationUnit` 中的声明顺序处理静态字段初始化，没有分析字段之间的依赖关系。若类 A 的静态初始化器引用了类 B 的静态字段，而 B 在源码中声明于 A 之后，则运行时读到未初始化值，产生难以排查的 bug。

**影响范围**：`FunctionEmitter.cs` `EmitStaticInit` 方法；所有跨类静态字段初始化的场景。

**建议改进**：在 `IrGen.Generate` 阶段构建静态字段依赖图（有向图），拓扑排序后再发射 `__static_init__` 函数，若存在循环依赖则报编译错误。

**参考**：Go 编译器的 `init graph` 排序机制；C++ 的 Static Initialization Order Fiasco 解决方案。

---

### 不足 6 🟡 错误恢复策略缺失

**问题描述**：`DiagnosticBag` 使用 sentinel 类型（`Z42Type.Error`、`Z42Type.Unknown`）让解析和类型检查在出错后继续运行，但没有明确的 **panic-mode 恢复** 或 **synchronization point** 规范。一处语法错误可能导致后续十几条假阳性诊断，用户面对大量无意义错误，实际有效信息被淹没。

**影响范围**：`z42.Syntax/Parser/`（Parser 层）；`z42.Semantics/TypeCheck/TypeChecker.cs`（TypeChecker 层）。

**建议改进**：
- **Parser 层**：定义 synchronization token 集合（`;` `}` `fn` `class`），出错后跳到最近同步点继续解析
- **TypeChecker 层**：每个函数独立隔离错误，一个函数绑定失败不影响其他函数的类型推断
```csharp
BoundBlock? TryBindFunction(FunctionDecl fn) {
    using var scope = _diags.BeginIsolatedScope();
    try { return BindBody(fn); }
    catch (FatalBindException) { scope.MarkFailed(); return null; }
}
```

**参考**：Rust compiler `Recover` trait；TypeScript `errorRecovery` token 跳过策略。

---

### 不足 7 🟡 测试覆盖不足（缺乏属性测试与 Fuzzing）

**问题描述**：测试基础设施仅有 Golden Tests（固定输入输出对比），缺少属性测试（Property-Based Testing）和模糊测试（Fuzzing）。随着语言特性增加，手写 golden 用例面临组合爆炸，且无法发现系统性边界 bug（如词法器对特定 Unicode 序列的处理、Parser 对深层嵌套表达式的栈溢出等）。

**影响范围**：`z42.Tests/` 测试项目；整个编译器管道的回归保障。

**建议改进**：

| 测试类型 | 性质 | 工具 |
|---------|------|------|
| Lexer round-trip | `tokenize → detokenize → tokenize` 结果一致 | FsCheck / CsCheck |
| Parser round-trip | `parse → pretty-print → parse` 产生等价 AST | FsCheck |
| IR well-formedness | 每个 use 有 def，每个 block 有唯一 terminator | `IrVerifier`（Debug 构建自动运行）|
| Feature 矩阵 | 每个特性开关的 golden 用例组合 | 参数化测试 |
| Fuzzing | 随机输入不应导致编译器崩溃（只应报错）| SharpFuzz / libFuzzer |

---

### 七大不足优先级一览

| # | 不足 | 严重度 | 工时 | 建议时机 |
|---|------|--------|------|---------|
| 1 | 缺少 Visitor 接口 | 🔴 高 | M | Phase 2 前必须完成 |
| 2 | TypeChecker 职责未隔离 | 🔴 高 | L | Phase 2 前必须完成 |
| 3 | 缺少优化 Pass 层 | 🔴 高 | M | 建框架即可，Phase 2 前 |
| 4 | 类型映射重复 | 🟠 中 | S | 立即，成本极低 |
| 5 | 静态初始化无拓扑排序 | 🟠 中 | M | Phase 1 后期 |
| 6 | 错误恢复策略缺失 | 🟡 低 | L | Phase 1 后期 |
| 7 | 测试覆盖不足 | 🟡 低 | M | 持续投入 |

> **S** = 小（<1天）  **M** = 中（1-3天）  **L** = 大（>3天）

---

*文档完结。*