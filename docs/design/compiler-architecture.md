# z42 编译器内部实现原理

> **目的**：记录 C# bootstrap 编译器的内部数据结构、算法、加载策略与关键设计决策。
> 让新接手者不必阅读大量源码即可理解"为什么这样设计"。
> 面向语言设计师和编译器开发者，不面向 z42 语言使用者。
>
> 使用者视角请看 `docs/design/language-overview.md`、`docs/design/compilation.md`。

---

## Pipeline 顶层流程

```
source.z42
  │
  ├── Lexer (TokenDefs + LexCombinators)          [z42.Syntax/Lexer]
  │      → Tokens
  │
  ├── Parser (Pratt + 组合子)                     [z42.Syntax/Parser]
  │      → CompilationUnit (AST, sealed record)
  │
  ├── TypeChecker (SymbolCollector + SymbolTable) [z42.Semantics/TypeCheck]
  │      → SemanticModel
  │      ↑ 读：ImportedSymbolLoader from TsigCache
  │
  ├── IrGen (FunctionEmitter + Ctx)               [z42.Semantics/Codegen]
  │      → IrModule (registers SSA-ish)
  │
  └── ZbcWriter / ZpkgWriter                       [z42.IR/BinaryFormat]
         → source.zbc / package.zpkg
```

单文件模式走 `SingleFileCompiler`；项目模式（`.z42.toml`）走 `PackageCompiler`，
两者共享 `PipelineCore` 的 TypeCheck + Codegen 阶段。

---

## TSIG 与跨包符号导入

### TSIG（Type Signature）section

zpkg 的 `TSIG` section 存储**包内所有 public 类型的结构化元数据**（类 / 接口 / 枚举 / 函数签名 / 约束），
供其他包 **编译期** 消费。运行时不使用 TSIG —— 运行时走 zbc 里的 `Function` + `TypeDesc`。

**形状（简化）：**

```csharp
sealed record ExportedModule(
    string                          Namespace,
    List<ExportedClassDef>          Classes,
    List<ExportedInterfaceDef>      Interfaces,
    List<ExportedEnumDef>           Enums,
    List<ExportedFunctionDef>       Functions
);
```

一个 zpkg 可含多个 `ExportedModule`（每个源文件一个）；同一包内不同 namespace 的源文件分别成组。

### TsigCache

位置：`src/compiler/z42.Pipeline/PackageCompiler.cs`

**职责**：编译器加载依赖包的 TSIG 元数据，按需供给 `ImportedSymbolLoader`。

**核心数据结构：**

```csharp
// namespace → list of zpkg full paths (2026-04-25 起改为 List<string>)
private readonly Dictionary<string, List<string>> _nsToPaths;
// zpkg path → 已加载 TSIG modules（懒加载 + 缓存）
private readonly Dictionary<string, List<ExportedModule>> _cache;
```

**加载流程：**

1. `ScanLibsForNamespaces`（在 `PackageCompiler.BuildTarget` 启动时）：扫描 `libs/` 下所有 `.zpkg`，
   读取每个 zpkg 的 `NSPC` section（namespaces 列表），对每个 namespace 调 `RegisterNamespace(ns, path)`。
2. `RegisterNamespace(ns, path)`：追加到 `_nsToPaths[ns]` 列表；**允许同一 namespace 多 zpkg 共享**。
3. `LoadForUsings(usings)` / `LoadAll()`：根据 `using` 声明（或全部已注册）聚合所有相关 zpkg 路径，
   首次访问时 `LoadZpkg` 解码 `TSIG` section，结果进 `_cache` 复用。

### 设计决策：namespace 可跨 zpkg（2026-04-25 vm-zpkg-dependency-loading）

**为什么**：对齐 C# assembly 模型 —— `System.Collections.Generic` 在 C# 里物理分布在 `System.Private.CoreLib`
和扩展 assembly。z42 stdlib 同样：`Std.Collections` 下的 `List<T>` / `Dictionary<K,V>` 驻留
`z42.core.zpkg`（实现 prelude 体验），而 `Queue<T>` / `Stack<T>` 驻留 `z42.collections.zpkg`。

**实现关键**：`_nsToPaths` 必须是 `Dictionary<string, List<string>>`（不是
`Dictionary<string, string>` + first-wins）；否则后来的 zpkg 被静默丢弃，导致
`QualifyClassName` 在用户代码引用 Stack / Queue 时找不到 namespace 映射、
IR 生成 bare 名 `Stack` 而非 FQ 名 `Std.Collections.Stack`，运行时 VCall 失败。

**对称性**：VM 侧 `LazyLoader` 同步支持多 zpkg 共享 namespace —— 两端必须一致。

---

## 符号解析与 QualifyClassName

### ImportedSymbolLoader

位置：`src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs`

**职责**：把 TSIG `ExportedModule` 列表合并为一组 `ImportedSymbols`，
供 `SymbolCollector` / `TypeChecker` 消费。

**关键字段：**
- `classes: Dictionary<string, Z42ClassType>` — 类名 → 重建后的类类型
- `classNs: Dictionary<string, string>` — 类名 → **简名到 namespace 的映射**（QualifyClassName 靠它）
- `interfaces`, `enumConsts`, `enumTypes`
- `classConstraints`, `funcConstraints` — L3-G3d 泛型约束（延迟解析到 bundle）

**冲突策略（first-wins）**：`if (!classes.ContainsKey(cls.Name))` —— 不同 zpkg 同名类保留先加载的。
预期同名冲突不应发生；若出现只在编译器层面 silent，运行时仍按 zbc func name 走。

### QualifyClassName

位置：`src/compiler/z42.Semantics/Codegen/IrGen.cs:58`

```csharp
string QualifyClassName(string className) {
    // local class shadows imported
    if (sem.Classes.ContainsKey(className) && !sem.ImportedClassNames.Contains(className))
        return QualifyName(className);
    return sem.ImportedClassNamespaces.TryGetValue(className, out var ns)
        ? $"{ns}.{className}" : QualifyName(className);
}
```

**作用**：ObjNew / Call / VCall 生成 IR 时把简名（用户代码写 `new Stack<int>()`）
resolve 到 FQ 名 `Std.Collections.Stack`，让 VM 能正确查 `func_index`。

**失败模式**：若 `sem.ImportedClassNamespaces` 没有该类（TsigCache 漏载、
TSIG 解码失败、namespace 过滤不含其 namespace 等），fallback 到
`QualifyName(className)`（用当前文件 namespace）→ 生成 bare 名 → 运行时
`function not found`。

---

## pseudo-class 策略与迁移

历史上 `Console` / `Math` / `Assert` / `Convert` / `String.*` 方法、`List<T>` / `Dictionary<K,V>`
由编译器的 **pseudo-class 机制**（`BuiltinTable.cs`）直接解析到 VM builtin，绕过 stdlib 加载。

**现状（2026-04-25）**：
- `Console` / `Math` / `Assert` / `Convert` / `String.*`：已迁移到 stdlib `.z42` 源码（L2 M7）
- `List<T>` / `Dictionary<K,V>`：已迁移到 `z42.core/src/Collections/`（L3-G4h step3）
- **残留兜底**：`SymbolCollector.cs:208-209` 仍有 `"List" => Z42PrimType("List")` 和
  `"Dictionary" => Z42PrimType("Dictionary")` 硬编码映射 —— 作为"TypeEnv.BuiltinClasses
  动态注入"未完成前的 bridge，计划在 L3-G 泛型类型表示扩展时清理（roadmap L2 backlog）

---

## DependencyIndex 与 Import 解析

位置：`src/compiler/z42.IR/DependencyIndex.cs`

**职责**：给定 `using Std.Collections;` 一条语句，TypeChecker 需要：
1. 确认该 namespace 存在（不存在 → 错误 Z1xxx）
2. 把该 namespace 标为"可见" → `ImportedSymbolLoader` 按此过滤 TSIG

`DepIndex`（`TypeChecker` 的构造参数）映射 namespace → 是否已知可用。
由 `ScanLibsForNamespaces` / `ScanZbcForNamespaces` 构建。

---

## Pratt 表达式解析

位置：`src/compiler/z42.Syntax/Parser/ExprParser.cs`

手写组合子（参考 Datalust/Superpower 设计），不引入外部 parser combinator 库
（为自举保留）。核心：

- **NudTable**（null denotation）：从哪个 token **开始** 表达式 —— 字面量、前缀运算符、`(`、`new`、lambda 等
- **LedTable**（left denotation）：在已有表达式后接什么 token —— 二元运算符、`.`（member）、`[`（index）、`(`（call）、`?:`、`switch`、postfix `++`/`--`

**优先级**（binding power）：见 `.claude/rules/compiler-csharp.md` 的 Pratt 表。

---

## 关键设计权衡

### 为什么 AST 节点是 `sealed record`

- **不可变 + 值相等**：便于并行分析、避免副作用
- **sealed**：匹配时 switch exhaustive，不漏 case
- **record**：自动合成 ctor / ToString / Equals，减少样板代码

### 为什么 TypeChecker 不直接写 IR

分离原因：
- TypeCheck 是**纯前端**（只看 AST + imports，产出 SemanticModel）
- Codegen 是**后端映射**（SemanticModel → IR）
- 两者通过 SemanticModel 接口解耦，便于将来加 LSP / incremental compilation

### 为什么不用 incremental parsing

z42 当前每次全量 parse。L3 后计划引入 LSP 时会考虑 incremental，但
bootstrap 阶段不做 —— 复杂度远超收益。

---

## 延伸阅读

- `docs/design/compilation.md` — 构建流程（manifest → zpkg 的用户视角）
- `docs/design/zbc.md` — `.zbc` 二进制格式
- `docs/design/namespace-using.md` — namespace / using 的语言规则
- `.claude/rules/compiler-csharp.md` — C# 编译器开发规范（代码风格 + AST / Parser / Lexer 约定）
