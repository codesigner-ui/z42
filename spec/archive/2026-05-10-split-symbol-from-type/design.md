# Design: Split Symbol Layer from Z42Type

## Architecture

```
                           ┌─────────────────────────────────┐
                           │  Z42.Syntax.Parser              │
                           │  FunctionDecl / FieldDecl       │
                           │  (immutable AST records)        │
                           └────────────┬────────────────────┘
                                        │ ref (back-pointer, nullable on imported)
                                        ▼
┌────────────────────────────────────────────────────────────────────────┐
│  Z42.Semantics.Symbols (NEW)                                            │
│                                                                          │
│  IMemberSymbol       (base interface)                                   │
│   ├── Name, Span, Visibility, ContainingClass: Z42ClassType             │
│   │                                                                      │
│   ├── IMethodSymbol                                                      │
│   │    └── MethodSymbol (sealed record)                                 │
│   │         Signature: Z42FuncType                                      │
│   │         Modifiers: FunctionModifiers                                │
│   │         Decl: FunctionDecl?       (null only if imported)           │
│   │         TestAttributes: IReadOnlyList<TestAttribute>?               │
│   │                                                                      │
│   └── IFieldSymbol                                                       │
│        └── FieldSymbol (sealed record)                                  │
│             Type: Z42Type                                               │
│             IsStatic, IsEvent: bool                                     │
│             Decl: FieldDecl?          (null only if imported)           │
└────────────────────────────────────────────────────────────────────────┘
                                        │
                                        │ Z42ClassType.Methods/Fields/...
                                        │   = IReadOnlyDictionary<string, IXxxSymbol>
                                        ▼
┌────────────────────────────────────────────────────────────────────────┐
│  Z42.Semantics.TypeCheck.Z42Type                                        │
│                                                                          │
│  Z42ClassType  (modified — dict value types only)                       │
│   ├── Name, TypeParams, BaseClassName  (type identity, unchanged)       │
│   ├── Fields:        IReadOnlyDictionary<string, IFieldSymbol>          │
│   ├── Methods:       IReadOnlyDictionary<string, IMethodSymbol>         │
│   ├── StaticFields:  IReadOnlyDictionary<string, IFieldSymbol>          │
│   └── StaticMethods: IReadOnlyDictionary<string, IMethodSymbol>         │
│                                                                          │
│  Z42InterfaceType  (modified — Methods dict value)                      │
│   └── Methods:       IReadOnlyDictionary<string, IMethodSymbol>         │
│                                                                          │
│  All other Z42Type records: unchanged                                   │
└────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌────────────────────────────────────────────────────────────────────────┐
│  Z42.Semantics.Bound                                                    │
│                                                                          │
│  BoundCall  (modified — direct method dispatch only)                    │
│   ├── Kind: Free | Static | Instance | Virtual                          │
│   └── Symbol: IMethodSymbol  (NEW, non-null)                            │
│                                                                          │
│  BoundIndirectCall  (NEW — function-value invocation)                   │
│   ├── Callee: BoundExpr  (lambda / ident-of-FuncType / member-of-       │
│   │                       FuncType / etc.)                              │
│   ├── Args, RetType, Span                                               │
│   └── (no Symbol — there's no method symbol for indirect calls)         │
│                                                                          │
│  BoundMember  (modified)                                                │
│   └── Symbol: IMemberSymbol?  (NEW, nullable for unresolved)            │
└────────────────────────────────────────────────────────────────────────┘
                                        │
                                        ▼
┌────────────────────────────────────────────────────────────────────────┐
│  Consumers (TypeChecker / IrGen / FunctionEmitter / TestAttribute      │
│             Validator / BoundDumper)                                    │
│                                                                          │
│  - All `cls.Methods[name]` lookups now return IMethodSymbol             │
│  - Access signature via `.Signature.ParamTypes` / `.Signature.Ret`      │
│  - Access decl via `.Decl?.Span` / `.Decl?.Modifiers`                   │
│  - TestIndex collection via `IMethodSymbol.TestAttributes` (no AST walk)│
└────────────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 单一 MethodSymbol 类型，imported 通过 `Decl=null` 区分

**问题**：本地方法（有 `FunctionDecl`）和 imported 方法（无 AST，只有 TSIG 元数据）如何在 Symbol 层表达？

**选项**：
- A. 两个 record：`MethodSymbol` (Decl 非空) + `ImportedMethodSymbol` (无 Decl)，调用方通过类型分支判断
- B. 单一 record `MethodSymbol`，`Decl: FunctionDecl?` 可空，imported 路径传 null
- C. IModifiersSource 抽象层

**决定**：**B**。`MethodSymbol`（和 `FieldSymbol`）是单一 sealed record，imported 时 `Decl == null`。

**理由**：
- 调用方零分支：`symbol.IsStatic` 走单一实现（读 `Modifiers` 字段）
- 类型数量减半：消费者不用区分 local / imported
- AST 不可变 + 构造时 `Modifiers` 字段从 `Decl.Modifiers` 拷贝 → 永不漂移
- "Decl 是初始权威源、Modifiers 字段是稳态权威源" 的语义清晰
- Roslyn 走的就是这个模式（`IMethodSymbol.IsStatic` 是接口 getter，背后细节不暴露）

### Decision 2: Z42InterfaceType.Methods 一并迁移

**问题**：当前 `Z42InterfaceType.Methods: IReadOnlyDictionary<string, MethodSignature>`，迁不迁？

**决定**：**一起迁移**。`Z42InterfaceType.Methods: IReadOnlyDictionary<string, IMethodSymbol>`。

**理由**：
- 类 / 接口对称：调用方在类和接口之间走相同访问模式
- 接口方法通常无 body，但可有 `static virtual` body（z42 已支持）；`MethodSymbol.Decl` 在接口路径上指向对应 `MethodSignature` 的 AST 节点 OR `null`（pure abstract method 无 body 时）
- 加 abstract 标记：`IMethodSymbol.IsAbstract` 通过 `Modifiers.HasFlag(Abstract)` 判断

**小问题**：`MethodSignature` 不是 `FunctionDecl`。两个选项：
- **i.** 给 `MethodSymbol.Decl` 类型放宽为 `Object?` 或新建 `IDecl` 基类（类型不安全 / 抽象激增）
- **ii.** `MethodSymbol.Decl` 仍是 `FunctionDecl?`，接口方法的 `Decl` 一律为 null；接口方法的 `Span` / `Modifiers` 直接取自 `MethodSignature` 在构造时

**子决定**：**ii**。接口方法 `Decl == null`，等同 imported 路径处理。`Span` / `Modifiers` / `TestAttributes` 在 `MethodSymbol` 自存字段中携带；如果未来需要"接口方法的源声明"，再引入 `IDecl` 或 `MethodSignature` back-pointer。

### Decision 3: BoundCall 直接 / 间接分离 (Option C)

**问题**：lambda / delegate 调用与方法分派调用是同一节点还是分开？

**决定**：**拆**。`BoundCall` 用于直接方法分派，`BoundIndirectCall` 用于函数值调用。

**理由**：
- 语义上确实是两类操作（Roslyn 也分开）
- `BoundCall.Symbol` 可以做到非空（强不变量），消费者零分支
- 间接调用的"被调用方"是个表达式（已有 `BoundExpr` 节点，自带 Span 和 Type），不需要虚构 IInvocationSymbol
- BoundExprVisitor 多一个 case，强制 visitor 子类编译期失败 → 全员 override 新 case，符合 visitor 框架核心收益

**代价**：
- BoundExprVisitor 基类 switch + 所有 visitor 子类增加 `VisitIndirectCall` —— 5 个 visitor 实现（IrEmitExprVisitor / FlowAnalyzer.ReadsVisitor / ClosureEscapeAnalyzer.EscapeExprScanner / BoundDumper.ExprDumper / FunctionEmitter.ClassRefScanner）
- 测试需要分类覆盖

### Decision 4: ContainingClass: Z42ClassType（不是 string）

**问题**：`IMemberSymbol.ContainingClass` 类型选择？

**决定**：**`Z42ClassType`**（完整引用）。

**理由**：
- 调用方常需要从 symbol 反查回 ClassType（找 sibling methods、查 BaseClass、检查接口实现），用 ClassType 直接取，不需要二次 dict lookup
- Z42ClassType 已是 record，引用安全
- 顶层自由函数：用 `null`（`ContainingClass: Z42ClassType?` 改为可空）—— `BoundCallKind.Free` 路径走这个

**循环引用问题**：`Z42ClassType.Methods` 持有 `IMethodSymbol`，`IMethodSymbol.ContainingClass` 持有 `Z42ClassType`。这是 OK 的：
- C# / .NET GC 处理循环引用无问题（不像 Rust 需要 Weak）
- 不影响序列化（Symbol 不进入 zbc）
- record `Equals` 默认按引用穿透 → 必须**手动重写** `MethodSymbol.Equals` 排除 `ContainingClass`，否则 `Z42ClassType.Equals` 会无限递归
  - 替代：`MethodSymbol` 不用 record（用 sealed class + 显式 Equals based on (ContainingClass.Name, Name, Signature)），更安全

**子决定**：`MethodSymbol` / `FieldSymbol` 用 **sealed class**（不是 record），手写 Equals/GetHashCode based on `(ContainingClass.Name, Name, Signature)`。Decl 是 back-pointer 不参与相等性。

### Decision 5: Z42FuncType 不加 BackingSymbol

**问题**：FuncType 是否反向指向 IMethodSymbol？

**决定**：**不加**。Z42FuncType 保持纯结构性。

**理由**：
- 类型是结构性概念，不该携带"来自哪里"的信息
- 加了会破坏 lambda↔delegate 互通（`Func<int,int>` from method group conversion 与 lambda 字面量类型不再相等）
- 真实需求都能通过表达式级 back-pointer 满足（`BoundIdent.Symbol` / `BoundCall.Symbol` 等）

### Decision 6: BoundIdent 是否也加 Symbol

**问题**：`var f = Foo;`（方法组转换）后 `f` 的 `BoundIdent` 是否携带方法 Symbol back-pointer？

**决定**：**第一阶段不加**。`BoundIdent` 不加 Symbol 字段。

**理由**：
- 方法组转换路径已经通过 BoundCall.Symbol 在 call site 解决（`f(...)` 是 BoundIndirectCall，但 `f` 这个 BoundIdent 的"它来自方法组"信息可以从 `f.Type is Z42FuncType` + 上下文推断）
- 如果 R-series 反射真的需要"`var f = Add` 中 f 的来源 = Add 方法"，独立 spec 加（`BoundIdent.SourceSymbol`）
- 控制本 spec scope

### Decision 7: 构造顺序 — Z42ClassType 与 Symbol 的循环依赖

**问题**：`Z42ClassType.Methods: dict<..., IMethodSymbol>` 而 `IMethodSymbol.ContainingClass: Z42ClassType`。先有鸡还是先有蛋？

**决定（Phase 1 实施时修订）**：**保留 `sealed record`，自定义 `Equals` 排除 member dicts**，使用 `with` / `Rebuild()` 重建。

**Phase 1 实施发现**：原计划"sealed class + SetMembers"路径不可行 —— `Z42Type` 是 `abstract record`（line 8），C# 规则 "Only records may inherit from records" 强制所有 Z42Type 子类必须保留 `record` 形式。转换 Z42Type 为 abstract class 会牵连 11 个 Z42Type 子类（PrimType / FuncType / ArrayType / OptionType / GenericParamType / EnumType / InstantiatedType / ...）的 record→class 大重构，远超本 spec 范围。

**修订实现**：
- Z42ClassType 保留 `sealed record` + 5 个 dict 仍在 positional ctor 中
- 自定义 `Equals(Z42ClassType?) => Name == other.Name && IsStruct == other.IsStruct`
  + `GetHashCode() => HashCode.Combine(Name, IsStruct)`
- 这样 Methods/Fields/StaticMethods/StaticFields 字典即使在 Phase 2 持有 IMethodSymbol（持有 Z42ClassType 反指针），也不会触发循环 Equals —— record 默认 Equals 被覆盖
- Phase 2 构造时：构造 ClassType skeleton（Methods=empty 等）→ 构造 MethodSymbol 引用它 → `with { Methods = symbolDict }` 重建 ClassType
- 添加 `Rebuild(...)` helper 简化重建（与 `with` expression 等价但参数命名更明确）
- 既有 SymbolCollector 3 个 `with`-site 不需修改 —— with-expression 在 record 上仍然可用

**关键不变量**：
1. Z42ClassType 是 record（不是 sealed class）
2. 自定义 Equals 仅按 (Name, IsStruct) 比对
3. with-expression 仍然可用

**子决定**（撤销）：原 "sealed class + single-shot SetMembers" 撤销。Phase 2 构造时若需要"先 skeleton 再装回成员"，使用 `with` 或 `Rebuild()` 重建。

### Decision 8: 迁移分阶段

为避免 27 文件 atomic commit，分 phase 推进：

- **Phase 1** — Symbol 基础设施（NEW 文件 + Z42ClassType skeleton 改造为 sealed class）
- **Phase 2** — SymbolCollector 构造 IMethodSymbol/IFieldSymbol（Decl 携带）；Z42ClassType.Methods/Fields 字典值切换；TypeChecker 49 处调用点机械迁移
- **Phase 3** — Codegen 14 处调用点 + ImportedSymbolLoader symbol 构造（Decl=null）
- **Phase 4** — BoundCall.Symbol + BoundIndirectCall（含 visitor 子类全员加 case）
- **Phase 5** — TestAttributeValidator + IrGen TestIndex 改用 IMethodSymbol.TestAttributes；BoundDumper 显示 Symbol 信息
- **Phase 6** — 文档同步 + 归档

每 Phase 独立 commit + GREEN 验证。Phase 4 是核心架构改动，预期最大。

## Implementation Notes

### 关键 API

```csharp
// src/compiler/z42.Semantics/Symbols/IMemberSymbol.cs
namespace Z42.Semantics.Symbols;

public interface IMemberSymbol
{
    string Name { get; }
    Span Span { get; }
    Visibility Visibility { get; }
    Z42ClassType? ContainingClass { get; }   // null for top-level free functions
}

public interface IMethodSymbol : IMemberSymbol
{
    Z42FuncType Signature { get; }
    FunctionModifiers Modifiers { get; }
    FunctionDecl? Decl { get; }              // null for imported / interface-abstract
    IReadOnlyList<TestAttribute>? TestAttributes { get; }

    bool IsStatic   => Modifiers.HasFlag(FunctionModifiers.Static);
    bool IsVirtual  => Modifiers.HasFlag(FunctionModifiers.Virtual);
    bool IsAbstract => Modifiers.HasFlag(FunctionModifiers.Abstract);
    bool IsOverride => Modifiers.HasFlag(FunctionModifiers.Override);
    bool IsExtern   => Modifiers.HasFlag(FunctionModifiers.Extern);
}

public interface IFieldSymbol : IMemberSymbol
{
    Z42Type Type { get; }
    bool IsStatic { get; }
    bool IsEvent { get; }
    FieldDecl? Decl { get; }
}

// MethodSymbol implementation (sealed class, manual Equals)
public sealed class MethodSymbol : IMethodSymbol
{
    public string Name { get; }
    public Span Span { get; }
    public Visibility Visibility { get; }
    public Z42ClassType? ContainingClass { get; }
    public Z42FuncType Signature { get; }
    public FunctionModifiers Modifiers { get; }
    public FunctionDecl? Decl { get; }
    public IReadOnlyList<TestAttribute>? TestAttributes { get; }

    public MethodSymbol(string name, Z42ClassType? containingClass, Z42FuncType signature,
        FunctionModifiers modifiers, Span span, Visibility visibility,
        FunctionDecl? decl = null, IReadOnlyList<TestAttribute>? testAttrs = null)
    {
        Name = name;
        ContainingClass = containingClass;
        Signature = signature;
        Modifiers = modifiers;
        Span = span;
        Visibility = visibility;
        Decl = decl;
        TestAttributes = testAttrs;
    }

    public override bool Equals(object? obj) =>
        obj is MethodSymbol o
        && Name == o.Name
        && (ContainingClass?.Name ?? "") == (o.ContainingClass?.Name ?? "")
        && Signature.Equals(o.Signature);

    public override int GetHashCode() => HashCode.Combine(
        Name, ContainingClass?.Name ?? "", Signature);
}
```

### Z42ClassType 改造

```csharp
// 原: public sealed record Z42ClassType(string Name, IReadOnlyDictionary<...> Fields, ...) : Z42Type
// 新:
public sealed class Z42ClassType : Z42Type
{
    public string Name { get; }
    public IReadOnlyList<string>? TypeParams { get; }
    public string? BaseClassName { get; }
    public bool IsStruct { get; }
    public IReadOnlySet<string>? EventFieldNames { get; }
    public bool HasArityMangle { get; }

    private IReadOnlyDictionary<string, IFieldSymbol> _fields = Empty<IFieldSymbol>();
    private IReadOnlyDictionary<string, IMethodSymbol> _methods = Empty<IMethodSymbol>();
    private IReadOnlyDictionary<string, IFieldSymbol> _staticFields = Empty<IFieldSymbol>();
    private IReadOnlyDictionary<string, IMethodSymbol> _staticMethods = Empty<IMethodSymbol>();
    private IReadOnlyDictionary<string, Visibility> _memberVisibility = Empty<Visibility>();
    private bool _frozen;

    public IReadOnlyDictionary<string, IFieldSymbol>  Fields        => _fields;
    public IReadOnlyDictionary<string, IMethodSymbol> Methods       => _methods;
    public IReadOnlyDictionary<string, IFieldSymbol>  StaticFields  => _staticFields;
    public IReadOnlyDictionary<string, IMethodSymbol> StaticMethods => _staticMethods;
    public IReadOnlyDictionary<string, Visibility>    MemberVisibility => _memberVisibility;

    public Z42ClassType(string name, ...) { /* identity fields only */ }

    /// Single-call setter for Phase C of two-phase construction.
    /// Throws if called twice.
    internal void SetMembers(
        IReadOnlyDictionary<string, IFieldSymbol> fields,
        IReadOnlyDictionary<string, IMethodSymbol> methods,
        IReadOnlyDictionary<string, IFieldSymbol> staticFields,
        IReadOnlyDictionary<string, IMethodSymbol> staticMethods,
        IReadOnlyDictionary<string, Visibility> memberVisibility)
    {
        if (_frozen) throw new InvalidOperationException(
            $"Z42ClassType `{Name}` already had its members set; SetMembers is single-shot");
        _fields = fields;
        _methods = methods;
        _staticFields = staticFields;
        _staticMethods = staticMethods;
        _memberVisibility = memberVisibility;
        _frozen = true;
    }

    // Equals based on Name only (per fix-z42type-structural-equality precedent)
    public override bool Equals(object? obj) =>
        obj is Z42ClassType o && Name == o.Name && IsStruct == o.IsStruct;
    public override int GetHashCode() => HashCode.Combine(Name, IsStruct);
}
```

**关键点**：
- `Equals` 仅按 Name + IsStruct 比对，避免循环引用 → 不会触发 Methods dict 比较 → 不会无限递归
- `SetMembers` 是 internal single-shot，外部观察到的依然是 readonly dict
- Phase A → Phase C 必然在同一 SymbolCollector 调用栈内完成，frozen 后永久不变

### BoundIndirectCall 引入

```csharp
// src/compiler/z42.Semantics/Bound/BoundExpr.cs (ADDED)
public sealed record BoundIndirectCall(
    BoundExpr Callee,
    IReadOnlyList<BoundExpr> Args,
    Z42Type RetType,
    Span Span) : BoundExpr(RetType, Span);

// src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs (MODIFIED)
public abstract class BoundExprVisitor<TResult>
{
    public TResult Visit(BoundExpr e) => e switch
    {
        // ... 既有 case ...
        BoundIndirectCall ic => VisitIndirectCall(ic),
        // ...
    };

    protected abstract TResult VisitIndirectCall(BoundIndirectCall ic);
    // ... 既有 abstract ...
}

public abstract class BoundExprWalker : BoundExprVisitor<Unit>
{
    protected override Unit VisitIndirectCall(BoundIndirectCall ic)
    {
        Visit(ic.Callee);
        foreach (var a in ic.Args) Visit(a);
        return default;
    }
}
```

5 个 visitor 子类全部加 `VisitIndirectCall`：
- `IrEmitExprVisitor`：emit indirect call IR（已有 `CallIndirectInstr` 路径）
- `ReadsVisitor` (FlowAnalyzer)：递归 Callee + Args
- `EscapeExprScanner` (ClosureEscapeAnalyzer)：递归 Callee（**非 callee 位置**——这点要注意，间接调用的 Callee 还是个值，不能算"安全 callee 位置"，倾向 `escape`）
- `ExprDumper` (BoundDumper)：dump 输出
- `ClassRefScanner` (FunctionEmitter)：no-op（保持原 BoundLambda 等不递归的语义）

### TypeChecker 调用点迁移模式

机械替换 `cls.Methods[name]` → `cls.Methods[name].Signature`：

```csharp
// Before:
var funcType = cls.Methods[name];           // Z42FuncType
var paramTypes = funcType.ParamTypes;

// After:
var symbol = cls.Methods[name];             // IMethodSymbol
var paramTypes = symbol.Signature.ParamTypes;
```

工具：写一个 `dotnet test` 脚本检测这种模式辅助 review。

### ImportedSymbolLoader 改造

`ImportedSymbolLoader` Phase 2 (member population) 内部：

```csharp
// Before: 直接构造 Z42FuncType 塞进字典
var methods = exportedClass.Methods.ToDictionary(
    m => m.Name,
    m => new Z42FuncType(...));

// After: 构造 MethodSymbol(decl: null) 塞进字典
var methods = exportedClass.Methods.ToDictionary(
    m => m.Name,
    m => (IMethodSymbol)new MethodSymbol(
        name: m.Name,
        containingClass: classType,         // Phase A 的 skeleton
        signature: new Z42FuncType(...),
        modifiers: m.Modifiers,             // 从 TSIG 读
        span: m.Span ?? Span.Empty,         // imported 可能无源 span
        visibility: m.Visibility,
        decl: null,                          // imported = null
        testAttrs: null));                  // imported 通常无 test attr
```

TSIG / ExportedClass 是否携带 `Modifiers` / `Span` 是另一事 —— 检查现有 wire format，必要时扩展（**留 design 检查项**）。

### TestAttributeValidator 改造

```csharp
// Before:
foreach (var cls in cu.Classes)
    foreach (var m in cls.Methods)
        if (m.TestAttributes is { } attrs)
            ValidateAttrs(attrs, m, ...);

// After:
foreach (var cls in symbolTable.Classes.Values)
    foreach (var m in cls.Methods.Values)
        if (m.TestAttributes is { } attrs)
            ValidateAttrs(attrs, m, ...);
```

行为等价：`m.TestAttributes` 在构造时从 `decl.TestAttributes` 拷贝。Symbol 路径让"imported test method"也走同样代码（虽然实际不会有）。

### Z42InterfaceType.Methods 迁移

`MethodSignature` → `IMethodSymbol`，构造时：

```csharp
// SymbolCollector 处理 interface 的 method signature:
var methodSymbol = new MethodSymbol(
    name: methodSig.Name,
    containingClass: interfaceType /* 改: ContainingClass 类型扩展 */ ,
    signature: BuildFuncTypeFrom(methodSig),
    modifiers: ComputeModifiers(methodSig),  // IsStatic / IsVirtual 从 sig
    span: methodSig.Span,
    visibility: Visibility.Public,
    decl: null,  // 接口 method 无 FunctionDecl（除非 static virtual with body —— 那种情况留 design 决定）
    testAttrs: null);
```

**子问题**：`IMemberSymbol.ContainingClass: Z42ClassType?` —— 接口方法的 ContainingClass 应该是什么类型？

- 选 a：扩展为 `IMemberSymbol.ContainingType: Z42Type?`（更通用，但泛型化抽象）
- 选 b：接口方法的 `ContainingClass = null`，再加 `ContainingInterface: Z42InterfaceType?` 字段
- 选 c：接口方法不进 IMethodSymbol 抽象，独立 IInterfaceMethodSymbol（重复抽象）

**子决定**：**a — `ContainingType: Z42Type?`**。最通用，避免重复抽象。Class 方法时 `ContainingType is Z42ClassType`，接口方法时 `is Z42InterfaceType`。调用方需要 ClassType 时模式匹配 `ContainingType as Z42ClassType`。

更新 IMemberSymbol 接口：

```csharp
public interface IMemberSymbol
{
    string Name { get; }
    Span Span { get; }
    Visibility Visibility { get; }
    Z42Type? ContainingType { get; }   // Z42ClassType | Z42InterfaceType | null (top-level)
}
```

## Testing Strategy

- **单元测试** [`SymbolLayerTests.cs`](../../../src/compiler/z42.Tests/SymbolLayerTests.cs) (NEW)：
  - `MethodSymbol_LocalClass_HasDeclBackPointer`
  - `MethodSymbol_Imported_HasNullDecl`
  - `MethodSymbol_Modifiers_FromDeclAtConstruction`
  - `MethodSymbol_ImportedHasModifiersFromTsig`
  - `MethodSymbol_Equals_BasedOnContainingNameAndSignature`
  - `Z42ClassType_SetMembers_IsSingleShot`
- **单元测试** [`SymbolDeclSpanTests.cs`](../../../src/compiler/z42.Tests/SymbolDeclSpanTests.cs) (NEW)：
  - `Method_DeclSpan_PointsToSourceLocation`
  - `Field_DeclSpan_PointsToSourceLocation`
  - `Symbol_AcrossPipeline_PreservesDecl` (parse → typecheck → access via Z42ClassType.Methods)
- **回归覆盖**：1176 既有 C# Tests + 312 VM golden 必须 100% 全绿
- **新增 BoundIndirectCall 行为测试**：
  - `BoundIndirectCall_LambdaInvocation_NotBoundCall`
  - `BoundIndirectCall_FunctionVariableInvocation_NotBoundCall`
  - `BoundCall_DirectMethod_HasNonNullSymbol`
- **TestAttributeValidator 行为不变**：现有 R1 测试全绿即证（迁移到 Symbol 路径不应改输出）
- **BoundDumper 输出新增 Symbol 信息**：现有 `BoundDumperTests.cs` 期望字符串扩展 `decl=...` 标记

## Risks

| 风险 | 影响 | 缓解 |
|---|---|---|
| **Z42ClassType 改 sealed class 破坏 record 自动 with-expression** | 中 | 检查现有 `Z42ClassType` 是否有 `with` 表达式使用；若有，改为显式 ctor 调用 |
| **Z42ClassType.Equals 改 Name+IsStruct 后破坏既有相等性假设** | 高 | 既有相等性已经按 Name 比对（fix-z42type-structural-equality 注释提到），sealed class 下显式实现等价 |
| **BoundIndirectCall 让 5 个 visitor 子类强制 override** | 低 | 这是 visitor 框架的设计目标；编译期失败强制全员关注，没有静默退化 |
| **ContainingType 为 Z42Type? 让调用方多了模式匹配** | 低 | 大部分调用方在 class 上下文，`as Z42ClassType` 模式匹配清晰 |
| **TSIG / ExportedClass 可能不携带 Modifiers / Span** | 中 | Phase 3 检查；缺失时为 imported 路径添加 wire format extension（不改 zbc 主格式，只扩 TSIG 元数据） |
| **63 处调用点机械迁移漏改** | 中 | dotnet build 编译期检查；任何遗漏会因为类型不匹配立即报错 |
| **MethodSymbol Equals 用 (ContainingClass.Name, Name, Signature) 在重载时冲突** | 低 | 重载方法的 Name 已 mangled (`Foo$2`)；Signature 也不同；不冲突 |
| **构造顺序 bug：先用 Symbol 后 SetMembers** | 中 | SetMembers 双重调用 throw；测试覆盖 SymbolCollector 标准路径 |

## Estimated Effort

约 4-5 天分 6 phase：
- Phase 1（基础设施 + Z42ClassType 改造）：0.7 天
- Phase 2（SymbolCollector + TypeChecker 49 处迁移）：1 天
- Phase 3（Codegen 14 处 + ImportedSymbolLoader）：0.7 天
- Phase 4（BoundCall.Symbol + BoundIndirectCall + 5 visitor 子类）：1 天
- Phase 5（TestAttributeValidator + BoundDumper）：0.5 天
- Phase 6（文档同步 + 归档）：0.3 天
- 集成验证 + 调试 buffer：0.8 天
