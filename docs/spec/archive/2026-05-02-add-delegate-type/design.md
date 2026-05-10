# Design: D1a — `delegate` 关键字 + 命名 delegate 类型

## Architecture

```
┌─────────── 编译期（C#） ──────────────┐
│                                        │
│  Lexer ─► [delegate keyword token]    │
│              │                         │
│              ▼                         │
│  Parser ─► DelegateDecl(name, params,  │
│              ret, span)                │
│              │                         │
│              ▼                         │
│  CompilationUnit.Delegates: List<>     │
│              │                         │
│              ▼                         │
│  SymbolCollector ─► SymbolTable        │
│        .Delegates: Dict<name, FuncType>│
│              │                         │
│              ▼                         │
│  TypeChecker.ResolveType(NamedType nt) │
│    1. _activeTypeParams?               │
│    2. SymbolTable.Delegates[nt.Name]   │
│       → 命中返回 Z42FuncType ✓         │
│    3. Classes / Interfaces / Enums     │
│              │                         │
│              ▼                         │
│  Lambda assign / call site：           │
│    BindAssign/BindVarDecl 把 lambda    │
│    type 与 delegate type RequireAssignable
│    （结构等价路径）                    │
└────────────────────────────────────────┘

VM 端：完全无感（delegate = FuncType 别名；
lambda → FuncRef/Closure/StackClosure 路径已就绪）
```

## Decisions

### Decision 1: delegate 类型 = `Z42FuncType`，不引入 `Z42DelegateType`

**问题**：是否给 delegate 单独一个 Z42 type variant？

**选项**：
- A: `Z42FuncType` 复用（简单 type alias）
- B: `Z42DelegateType` 独立 variant，记录 delegate 名

**决定**：**选 A**。理由：

1. `SymbolCollector` 已经把 `Action<>` / `Func<>` desugar 为 `Z42FuncType` —— delegate 视作类似的语法糖（命名版）保持一致
2. 运行时 callable 三件套（`FuncRef` / `Closure` / `StackClosure`）已就绪，跟 delegate 名无关
3. 选 B 会让 `RequireAssignable` 规则爆炸（delegate vs FuncType vs lambda literal 三向兼容性矩阵）
4. 用户体验：诊断信息可以仍打印 delegate 名（保留 hint），TypeChecker 内部处理时按 FuncType 比对

> 副作用：`f.GetType()` 反射返回的是 FuncType-shaped 描述，不是 delegate 名。L3-R 反射阶段再考虑是否补充 delegate 名 metadata。

### Decision 2: 命名 delegate vs `(T) -> R` 字面量结构等价

**问题**：`delegate int Sq(int x)` 和 `(int) -> int` 应该是同一类型，还是不同类型？

**决定**：**结构等价（同一类型）**。

理由：
1. 与 C# 行为一致（"delegate 是 type，(T) -> R 是 type 的另一种 spelling"）
2. lambda → delegate / lambda → 字面量 走同一 RequireAssignable 路径，不需要特殊处理
3. 互转无开销 —— delegate 名只是 IDE / 错误诊断的 hint，不影响类型相等性
4. 与 K3 / K4（"类型决定 cardinality"）的精神一致 —— 类型系统按结构判定

### Decision 3: 解析位置 — 顶层 + 嵌套（class body 内）双支持

**问题**：delegate 能否出现在 class body 内（嵌套类型）？

**决定**：**支持**（user 2026-05-02 裁决）。

理由：
1. C# 行为对齐 —— 用户能直接用 `Btn.OnClick` 风格 API
2. 嵌套 delegate 与 enum / interface 嵌套不同 —— delegate 只是带 type-param 的 callable 别名，不是真实运行时类型，无 IR / VM 影响
3. 实现：`ClassDecl` 加 `List<DelegateDecl> NestedDelegates` 字段；`ParseClassDecl` 成员循环识别 `Delegate` token 走相同 ParseDelegateDecl
4. 外部引用：`Btn.OnClick` —— TypeChecker 在 NamedType 解析时按 dotted path 查 SymbolTable.Delegates（key 用 fully-qualified name `Btn.OnClick$N`）

### Decision 4: 支持泛型 delegate + where 约束（user 裁决）

**问题**：D1a 是否包含泛型 delegate？

**决定**：**包含**。理由：

1. 泛型 delegate 是必需 —— `Func<T,R>` / `Action<T>` 等都泛型，stdlib 真实类型（D1c）依赖 D1a 已 GREEN
2. z42 现有 generic class / func 路径成熟（L3-G2 已稳定），delegate 复用 `_activeTypeParams` + `ResolveType GenericType` 路径
3. where-clause 也复用 `WhereClause` 现有解析 + `ValidateGenericConstraints` 验证机制
4. 用户的话：泛型 + 约束一起加；不分两次 spec

> N arity 脚本生成（`tools/gen-delegates.z42`）属于 stdlib 内容，留 D1c。

### Decision 5: `(T) -> R` literal 路径不变 —— 不强制用 delegate 替代

**问题**：是否在 D1a 后强制用户改用 delegate？

**决定**：**不强制**。理由：

1. `(T) -> R` 字面量在闭包路径已大规模使用（mono / escape spec 测试都用），破坏成本高
2. delegate 是"命名版本"，与字面量等价；让用户按需选择
3. C# 也是两者并存（Func<T,R> + 自定义 delegate）

### Decision 6: SymbolTable.Delegates value type

`DelegateInfo`（与 D1c 原方案合并）：

```csharp
public sealed record DelegateInfo(
    Z42FuncType Signature,                       // 含 Z42GenericParamType 占位
    IReadOnlyList<string> TypeParams,
    IReadOnlyDictionary<string, GenericConstraintBundle>? Constraints,
    string? ContainerClass = null);              // 嵌套时持类名，否则 null
```

### Decision 7: 同名多 arity 用 `name$N` key

**问题**：`Action<T>` 和 `Action<T1,T2>` 同名不同 arity，SymbolTable key 怎么存？

**选项**：
- A: `Dictionary<string, DelegateInfo>` 每条单独 key，用 `Action$1` / `Action$2` 后缀 arity
- B: `Dictionary<string, List<DelegateInfo>>` 用 List 存所有 arity 的同名 delegate
- C: `Dictionary<(string, int), DelegateInfo>` 用 (name, arity) 元组 key

**决定**：**选 A**（`name$N` key）。理由：

1. 与现有 generic class / func 重载机制一致（`Methods["MyFunc$2"]` 风格 — codebase 多处用此约定）
2. ResolveType 在 NamedType 路径需要 `Action`（无 arity）→ 不命中（无 0-arity Action 时）；GenericType 路径根据 `gt.TypeArgs.Count` 拼出 `Action$N` 查找
3. 命名诊断信息可显示 "delegate `Action<T,T2>` (Action$2)" 与 generic 类型一致

### Decision 8: `delegate*<T,R>` unmanaged 语法预留

**问题**：未来要兼容 C# `delegate*<T,R>` unmanaged func pointer，怎样不挖坑？

**决定**：v1 Parser 在 `delegate` token 后立即检测 `*`：若有 `*`，抛 `delegate* (unmanaged function pointer) is not yet supported`。

理由：
1. `delegate*` 与正常 delegate 是两个 IR / VM 层路径（unmanaged 是裸 fn pointer，无 GC tracking、无 closure capture）
2. 现在留好提示，未来添加时只需替换报错为实际解析；现有 grammar 不需改
3. 不引入 token-level 二义性

## Implementation Notes

### Lexer 侧

```csharp
// TokenKind.cs
public enum TokenKind {
    // ... existing ...
    Delegate,   // NEW
}

// TokenDefs.cs::KeywordDefs
new("delegate", TokenKind.Delegate, LanguagePhase.Phase1),
```

### Parser 侧

```csharp
// Ast.cs
public sealed record DelegateDecl(
    string Name,
    Visibility Visibility,
    List<Param> Params,
    TypeExpr ReturnType,
    Span Span,
    List<string>? TypeParams = null,
    WhereClause? Where = null) : Item;

// CompilationUnit
public sealed record CompilationUnit(
    string? Namespace,
    List<string> Usings,
    List<ClassDecl> Classes,
    List<InterfaceDecl> Interfaces,
    List<FunctionDecl> Functions,
    List<EnumDecl> Enums,
    List<DelegateDecl> Delegates,   // NEW — 顶层 delegate
    List<ImplBlock> Impls,
    Span Span);

// ClassDecl 增加 NestedDelegates 字段（D1a Decision 3）
public sealed record ClassDecl(
    /* ... existing ... */,
    List<DelegateDecl> NestedDelegates,   // NEW
    Span Span,
    /* ... */);
```

`ParseDelegateDecl` 实现：

```csharp
private static DelegateDecl ParseDelegateDecl(
    ref TokenCursor cursor, Visibility vis, Span startSpan)
{
    ExpectKind(ref cursor, TokenKind.Delegate);

    // D1a Decision 8: unmanaged func pointer 预留
    if (cursor.Current.Kind == TokenKind.Star)
        throw new ParseException(
            "delegate* (unmanaged function pointer) is not yet supported",
            cursor.Current.Span);

    var retType = TypeParser.Parse(cursor).Unwrap(ref cursor);
    var name    = ExpectKind(ref cursor, TokenKind.Identifier).Text;

    // D1a Decision 4: 泛型 type-params 可选
    List<string>? typeParams = null;
    if (cursor.Current.Kind == TokenKind.Lt)
        typeParams = ParseTypeParamList(ref cursor);   // 复用现有 helper

    ExpectKind(ref cursor, TokenKind.LParen);
    var parms = ParseParamList(ref cursor);
    ExpectKind(ref cursor, TokenKind.RParen);

    // D1a Decision 4: where-clause 可选
    WhereClause? where = null;
    if (cursor.Current.Kind == TokenKind.Where)
        where = ParseWhereClause(ref cursor);          // 复用现有

    ExpectKind(ref cursor, TokenKind.Semicolon);
    return new DelegateDecl(name, vis, parms, retType,
        startSpan.Merge(cursor.Current.Span), typeParams, where);
}
```

顶层主循环 + ParseClassDecl 成员循环都加同一分支：

```csharp
// 顶层（TopLevelParser.cs）
if (cursor.Current.Kind == TokenKind.Delegate) {
    delegates.Add(ParseDelegateDecl(ref cursor, vis, span));
    continue;
}

// 类内（TopLevelParser.Types.cs::ParseClassDecl 成员循环）
if (cursor.Current.Kind == TokenKind.Delegate) {
    nestedDelegates.Add(ParseDelegateDecl(ref cursor, vis, span));
    continue;
}
```

### TypeChecker / SymbolTable

```csharp
// SymbolTable.cs
public sealed class SymbolTable {
    // ... existing ...
    public IReadOnlyDictionary<string, DelegateInfo> Delegates { get; }
    // key 形式：
    //   非泛型顶层：     "Foo"           （arity = 0）
    //   泛型顶层：       "Foo$N"         （N = TypeParams.Count）
    //   嵌套：           "Btn.OnClick"    或 "Btn.OnClick$N"
}

// SymbolCollector.cs::Collect — 顶层
foreach (var d in cu.Delegates)
    RegisterDelegate(d, containerClass: null);

// 嵌套
foreach (var cls in cu.Classes)
    foreach (var d in cls.NestedDelegates)
        RegisterDelegate(d, containerClass: cls.Name);

private void RegisterDelegate(DelegateDecl d, string? containerClass)
{
    var prev = _activeTypeParams;
    _activeTypeParams = d.TypeParams != null
        ? new HashSet<string>(d.TypeParams) : null;
    try
    {
        var paramTypes = d.Params.Select(p => ResolveType(p.Type)).ToList();
        var retType    = ResolveType(d.ReturnType);
        var sig        = new Z42FuncType(paramTypes, retType);

        var arity = d.TypeParams?.Count ?? 0;
        var bareName = containerClass is null ? d.Name : $"{containerClass}.{d.Name}";
        var key  = arity > 0 ? $"{bareName}${arity}" : bareName;
        delegates[key] = new DelegateInfo(sig, d.TypeParams ?? Array.Empty<string>(),
            constraints: null /* 由 GenericResolve pass 填充 */, containerClass);
    }
    finally { _activeTypeParams = prev; }
}
```

`ResolveType` 调整 —— NamedType / GenericType 路径都查 delegates：

```csharp
// NamedType（非泛型 / 0-arity 泛型）
NamedType nt when delegates.TryGetValue(nt.Name, out var info)
              && info.TypeParams.Count == 0
                => info.Signature,

// GenericType
GenericType gt when delegates.TryGetValue($"{gt.Name}${gt.TypeArgs.Count}", out var info)
                => SubstituteTypeParams(info.Signature, BuildSubMap(info.TypeParams, gt.TypeArgs)),
```

where-clause 解析跟 generic class / func 同一 pass（`TypeChecker.GenericResolve.cs::ResolveAllWhereConstraints`）—— 把 delegate 也纳入遍历范围。

### Codegen / VM / IR

**全部零变更**。lambda → delegate 走 BoundLambda / BoundFuncRef 现有路径（mono spec 已补齐 BoundIdent → LoadFn）；call site `d(args)` 走 BindCall 的 var-of-FuncType 分支。

## Testing Strategy

### 单元测试（C#）

`src/compiler/z42.Tests/DelegateDeclParserTests.cs`（NEW）：

1. `Simple_Delegate_Declaration_Parses` — `public delegate void Foo(int x);` 产生 DelegateDecl
2. `Delegate_With_Return_Type_Parses` — `public delegate int Sq(int x);`
3. `Delegate_Without_Params_Parses` — `public delegate void Done();`
4. `Generic_Delegate_Rejected_In_D1a` — `public delegate R Func<T,R>(T x);` 报错
5. `Nested_Delegate_In_Class_Rejected` — 类体内 delegate 报错

`src/compiler/z42.Tests/DelegateDeclTypeCheckTests.cs`（NEW）：

6. `Delegate_Resolves_To_FuncType` — `delegate int Sq(int x);` 的 NamedType 解析为 Z42FuncType
7. `Lambda_Assignable_To_Named_Delegate` — `Sq f = (int x) => x * x;` 通过
8. `Type_Mismatch_Rejected` — 签名不匹配 lambda → 错误
9. `Named_Delegate_And_Literal_Equivalent` — `(int) -> int` ↔ `delegate int Sq(int x);` 双向赋值通过

### Golden test

`src/runtime/tests/golden/run/delegate_d1a/source.z42`：

```z42
namespace Demo;
using Std.IO;

public delegate int Sq(int x);
public delegate void OnClick(int x, int y);

void Main() {
    Sq sq = (int x) => x * x;
    Console.WriteLine(sq(5));               // 25

    int n = 10;
    Sq add = (int x) => x + n;
    Console.WriteLine(add(7));              // 17

    OnClick handler = (int x, int y) => Console.WriteLine($"clicked at ({x},{y})");
    handler(3, 4);                           // clicked at (3,4)
}
```

期望输出：
```
25
17
clicked at (3,4)
```

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # +9
./scripts/regen-golden-tests.sh
./scripts/test-vm.sh                                   # +1×2 modes
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| `delegate` 关键字与现有标识符冲突（用户类 / 函数命名 `delegate`） | 全 stdlib + workspace grep 一遍；预期无命中（一直是保留词风格命名） |
| 解析时机和 enum / class 分支顺序冲突 | 在 main loop 显式 `case TokenKind.Delegate` 分支；与 `Enum` / `Interface` / `Class` 并列 |
| 现有 SymbolCollector `Action`/`Func` 硬编码 desugar 与新 SymbolTable.Delegates 重叠 | D1a 不动 hardcoded 路径；用户若声明自己的 `delegate void Action(...)` 会被 hardcoded 路径优先（也是合理的） |
| 嵌套 delegate 用户错误 | Parser 在 class body 内 ParseClassDecl 路径不识别 `delegate` 关键字 → 抛清晰错误 |
| 与已有 LambdaTypeCheckTests / ClosureCaptureTypeCheckTests 冲突 | 测试源不引入 delegate 关键字时行为完全不变（grep 验证） |
