# Spec: Symbol Layer

## ADDED Requirements

### Requirement: Symbol records carry declaration identity

Z42 编译器必须区分**类型身份**与**声明身份**：`Z42ClassType` 仅承载类型形状（Name / TypeParams / BaseClassName），所有成员声明（field / method / static field / static method）通过独立的 `IMethodSymbol` / `IFieldSymbol` 对象表达；这些 Symbol 对象同时承载从声明到原始 AST 节点的 back-pointer（`Decl: FunctionDecl?` / `FieldDecl?`），用于诊断、R-series 反射和 IDE 工具链。

#### Scenario: 本地类成员构造为 MethodSymbol，携带 Decl 反指针

- **WHEN** SymbolCollector 处理本地 `ClassDecl Foo { int Add(int a, int b) { ... } }`
- **THEN** `Z42ClassType("Foo").Methods["Add"]` 返回 `IMethodSymbol`
- **AND** `symbol.Decl` 引用源 `FunctionDecl`（`!= null`）
- **AND** `symbol.Span` 等于源 `FunctionDecl.Span`
- **AND** `symbol.Signature.ParamTypes` 等于 `[int, int]`
- **AND** `symbol.Signature.Ret` 等于 `int`
- **AND** `symbol.Modifiers` 等于源 `decl.Modifiers`（构造时拷贝，永不漂移）
- **AND** `symbol.ContainingClass` 直接引用 `Z42ClassType("Foo")`

#### Scenario: imported 类成员构造为 MethodSymbol，Decl 为 null 但其余字段完整

- **WHEN** ImportedSymbolLoader 加载来自 `Std.Collections.zpkg` 的 `class List<T>`
- **THEN** 对应的 `Z42ClassType("List").Methods["Add"]` 返回 `IMethodSymbol`
- **AND** `symbol.Decl == null`
- **AND** `symbol.Span` 等于 imported 元数据中记录的声明位置（或 `Span.Empty` 占位）
- **AND** `symbol.Signature` 完整携带签名（来自 TSIG）
- **AND** `symbol.Modifiers` 完整携带修饰符（来自 TSIG）
- **AND** `symbol.IsStatic` / `symbol.IsVirtual` 等便利 getter 与 local 路径返回值一致

#### Scenario: 字段同样有 IFieldSymbol 与 Decl 反指针

- **WHEN** SymbolCollector 处理 `class Bar { string Name; }`
- **THEN** `Z42ClassType("Bar").Fields["Name"]` 返回 `IFieldSymbol`
- **AND** `symbol.Decl` 引用源 `FieldDecl`
- **AND** `symbol.Type` 等于 `Z42PrimType("string")`
- **AND** `symbol.IsStatic == false`
- **AND** `symbol.IsEvent == false`

#### Scenario: 接口方法同步迁移到 IMethodSymbol

- **WHEN** SymbolCollector 处理 `interface IFoo { int Bar(int x); }`
- **THEN** `Z42InterfaceType("IFoo").Methods["Bar"]` 返回 `IMethodSymbol`
- **AND** `symbol.Decl` 引用源 `MethodSignature` 关联的 AST（若有 body 走 FunctionDecl 路径，无 body 走 MethodSignature 路径——design 决定）

### Requirement: BoundCall 直接调用与间接调用分离

Z42 编译器的 BoundExpr 树必须区分**方法分派**调用和**函数值**调用，不再用同一节点表达两类语义不同的操作；新增 `BoundIndirectCall` 节点承载 lambda / delegate / 闭包 / 函数指针的间接调用。

#### Scenario: 直接方法调用产生 BoundCall + 非空 Symbol

- **WHEN** TypeChecker 处理 `Foo.Bar(1, 2)`（其中 `Bar` 是 `Foo` 的静态方法）
- **THEN** 产生 `BoundCall` 节点
- **AND** `BoundCall.Kind == Static`
- **AND** `BoundCall.Symbol != null`
- **AND** `BoundCall.Symbol` 是 `Foo` 的 `Bar` 方法的 `IMethodSymbol`
- **AND** `BoundCall.Symbol.Signature` 与 `BoundCall` 的 RetType 等签名信息一致

#### Scenario: 实例方法 / 虚方法 / 自由函数调用同样产生 BoundCall + 非空 Symbol

- **WHEN** TypeChecker 处理 `obj.Method(args)` 或 `Method(args)`
- **THEN** 产生 `BoundCall` 节点，`Symbol` 非空
- **AND** `Kind ∈ {Instance, Virtual, Free}` 时 `Symbol` 始终非空
- **AND** `Kind == Free` 时 `Symbol.ContainingClass == null`（顶层函数无 containing class）—— design 决定具体如何表达

#### Scenario: lambda 直接调用产生 BoundIndirectCall

- **WHEN** TypeChecker 处理 `((int x) => x + 1)(3)`
- **THEN** 产生 `BoundIndirectCall` 节点
- **AND** `BoundIndirectCall.Callee` 是 `BoundLambda`
- **AND** `BoundIndirectCall.Args == [BoundLitInt(3)]`
- **AND** `BoundIndirectCall.RetType == int`

#### Scenario: 函数值变量调用产生 BoundIndirectCall

- **WHEN** TypeChecker 处理 `var f = (int x) => x + 1; f(3);`
- **THEN** `f(3)` 产生 `BoundIndirectCall`
- **AND** `Callee` 是 `BoundIdent("f")`
- **AND** `Callee.Type` 是 `Z42FuncType((int,) -> int)`

#### Scenario: 方法组转换的间接调用产生 BoundIndirectCall

- **WHEN** TypeChecker 处理 `var f = Foo.Bar; f(3);`（`Bar` 是方法组转换为函数值）
- **THEN** `f(3)` 产生 `BoundIndirectCall`，**而不是** BoundCall
- **AND** 因为 `f` 是函数值，不是方法引用——即使来源是方法组

#### Scenario: 闭包间接调用产生 BoundIndirectCall

- **WHEN** TypeChecker 处理 capturing lambda 调用：`int x = 10; var f = () => x; f();`
- **THEN** `f()` 产生 `BoundIndirectCall`（VM 走 `MkClos` + indirect call 路径）

### Requirement: 成员查询调用点全部通过 Symbol 访问

所有 `Z42ClassType.Methods[...]` / `Z42ClassType.Fields[...]` / `Z42InterfaceType.Methods[...]` 的查询调用点（共 ~63 处，分布在 9 个文件）必须从原来的"直接拿 `Z42FuncType` / `Z42Type`"改为"拿 `IMethodSymbol` / `IFieldSymbol` 后访问 `.Signature` / `.Type`"。

#### Scenario: TypeChecker 成员查询返回 Symbol

- **WHEN** TypeChecker.Exprs.Members 处理 `obj.Method`
- **THEN** 内部从 `cls.Methods[name]` 拿到 `IMethodSymbol`
- **AND** 用 `symbol.Signature.ParamTypes` / `.Ret` 进行 overload resolution
- **AND** 不再有任何代码直接从 `cls.Methods[name]` 期望 `Z42FuncType` 类型

#### Scenario: Codegen 成员查询返回 Symbol

- **WHEN** FunctionEmitterExprs.Members 检查 `cls.Methods.ContainsKey("foo")`
- **THEN** 行为不变（仅判断成员是否存在）
- **AND** 若需要进一步访问签名，通过 `symbol.Signature` 而非 `cls.Methods[name]` 直接当 FuncType

### Requirement: TestAttributeValidator 通过 Symbol 访问

R1 TestIndex 与 TestAttributeValidator 必须不再 re-walk `cu.Classes` AST 来获取 `[Test]` 等 attribute 信息，改为通过 `IMethodSymbol.TestAttributes` 访问；这是验证 Decl 反指针有效的核心场景。

#### Scenario: TestAttributeValidator 通过 Symbol 取得 attributes

- **WHEN** TestAttributeValidator 处理一个 [Test] 装饰的方法
- **THEN** 从 `IMethodSymbol.TestAttributes` 读取属性列表
- **AND** **不再** 通过 `cu.Classes[...].Methods[...].TestAttributes` AST 路径
- **AND** local 与 imported test 方法（imported 通常无 test attribute，但 API 一致）的访问路径相同

#### Scenario: IrGen TestIndex 收集通过 Symbol

- **WHEN** IrGen.Generate 收集 TestIndex
- **THEN** 通过 `IMethodSymbol.TestAttributes` 而非 `cu.Classes` 遍历
- **AND** 行为完全等价（生成的 TestIndex 与现版本字节一致）

### Requirement: BoundDumper 输出包含 Symbol 信息

`BoundDumper`（impl-dump-ast 引入）必须在 `BoundCall` / `BoundMember` 节点输出中显示 Symbol 的关键身份信息（`Decl?.Span` 或 imported 标记），以便调试时能立即看出"这次调用的目标声明在哪里"。

#### Scenario: BoundCall dump 显示 Symbol Decl 位置

- **WHEN** BoundDumper 处理 `BoundCall` 调用某 local 方法
- **THEN** 输出含 `decl=file.z42:5:10` 形式的位置标记
- **OR** imported 方法时含 `decl=imported` 标记

## MODIFIED Requirements

### Requirement: Z42ClassType 字典值类型

**Before:**
- `Z42ClassType.Fields: IReadOnlyDictionary<string, Z42Type>`
- `Z42ClassType.Methods: IReadOnlyDictionary<string, Z42FuncType>`
- `Z42ClassType.StaticFields: IReadOnlyDictionary<string, Z42Type>`
- `Z42ClassType.StaticMethods: IReadOnlyDictionary<string, Z42FuncType>`

**After:**
- `Z42ClassType.Fields: IReadOnlyDictionary<string, IFieldSymbol>`
- `Z42ClassType.Methods: IReadOnlyDictionary<string, IMethodSymbol>`
- `Z42ClassType.StaticFields: IReadOnlyDictionary<string, IFieldSymbol>`
- `Z42ClassType.StaticMethods: IReadOnlyDictionary<string, IMethodSymbol>`

`Z42InterfaceType.Methods` 同步变化。

### Requirement: BoundCall 节点字段

**Before:**
```csharp
public sealed record BoundCall(
    BoundCallKind Kind,
    BoundExpr? Receiver,
    string? ReceiverClass,
    string? MethodName,
    string? CalleeName,
    IReadOnlyList<BoundExpr> Args,
    Z42Type RetType,
    Span Span) : BoundExpr(RetType, Span);
```

**After:**
```csharp
public sealed record BoundCall(
    BoundCallKind Kind,
    BoundExpr? Receiver,
    string? ReceiverClass,
    string? MethodName,
    string? CalleeName,
    IReadOnlyList<BoundExpr> Args,
    Z42Type RetType,
    IMethodSymbol Symbol,            // ADDED — non-null for direct dispatch
    Span Span) : BoundExpr(RetType, Span);
```

新增 `BoundIndirectCall`：

```csharp
public sealed record BoundIndirectCall(
    BoundExpr Callee,                // 函数值表达式（lambda / ident / member 等）
    IReadOnlyList<BoundExpr> Args,
    Z42Type RetType,
    Span Span) : BoundExpr(RetType, Span);
```

### Requirement: BoundMember 节点字段

**Before:**
```csharp
public sealed record BoundMember(BoundExpr Target, string MemberName, Z42Type Type, Span Span)
    : BoundExpr(Type, Span);
```

**After:**
```csharp
public sealed record BoundMember(
    BoundExpr Target,
    string MemberName,
    IMemberSymbol? Symbol,           // ADDED — null only for unresolved or future-extension cases
    Z42Type Type,
    Span Span) : BoundExpr(Type, Span);
```

## IR Mapping

无新 IR 指令；本 spec 不改 IR 层。Symbol 是 C# 编译期/语义层抽象，对 `IrGen` 输出零影响。

`BoundIndirectCall` 与现有 `BoundCall(Kind=Indirect ?)` 在 codegen 端走相同的 `CallIndirect` IR 指令路径。

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [ ] Lexer — 不影响
- [ ] Parser / AST — 不影响
- [x] **TypeChecker** — 成员查询 ~49 处迁移；BoundCall 构造点新增 Symbol 字段；间接调用路径产生 BoundIndirectCall
- [x] **IR Codegen** — 成员查询 ~14 处迁移；TestIndex 改用 IMethodSymbol.TestAttributes
- [ ] VM interp — 零影响（Symbol 不进入 IR / runtime）
- [ ] zbc / zpkg wire format — 不影响
