# Design: D1c — 泛型 delegate + stdlib `Action` / `Func` / `Predicate`

## Architecture

```
┌── 编译期（C#）─────────────────────────┐
│ Parser ─► DelegateDecl(name, params,   │
│             ret, type_params=[T,R])    │
│              │                         │
│              ▼                         │
│ SymbolCollector ─► SymbolTable         │
│   .Delegates: Dict<name, DelegateInfo> │
│      DelegateInfo {                    │
│        Signature: Z42FuncType (含     │
│          Z42GenericParamType("T"))    │
│        TypeParams: ["T", "R"]          │
│      }                                 │
│              │                         │
│              ▼                         │
│ ResolveType(GenericType "Func<int,int>")│
│   1. SymbolTable.Delegates["Func"] →   │
│        DelegateInfo                    │
│   2. substitute T → int, R → int       │
│   3. → Z42FuncType([int], int)         │
└─────────────────────────────────────────┘

stdlib（z42.core/src/Delegates.z42）：
  public delegate void Action();
  public delegate void Action<T>(T arg);
  public delegate void Action<T1, T2>(T1 a, T2 b);
  public delegate void Action<T1, T2, T3>(T1 a, T2 b, T3 c);
  public delegate void Action<T1, T2, T3, T4>(T1 a, T2 b, T3 c, T4 d);
  public delegate R Func<T, R>(T arg);
  public delegate R Func<T1, T2, R>(T1 a, T2 b);
  ... (1-4 arity)
  public delegate bool Predicate<T>(T arg);
```

## Decisions

### Decision 1: 一步清理 hardcoded desugar

**问题**：SymbolCollector 现有 `Action`/`Func` desugar 是否分阶段删？

**决定**：**一步清理**。理由：

1. 双路径并存容易冲突 —— stdlib delegate 注册的 `Func<T,R>` 与 hardcoded `Func` 都返回 Z42FuncType，但前者经过 SymbolTable.Delegates 通道更整洁
2. 用户代码 `Func<int,int>` 在改后路径仍工作（stdlib 提供）
3. 测试基线（LambdaTypeCheckTests）必须全绿，验证替换无破坏

> 如果实施时发现 stdlib 加载顺序与 ResolveType 调用时机有冲突（例如 ResolveType 跑在 stdlib delegate 注册之前），就退回保留 fallback；记入 tasks.md 备注。

### Decision 2: DelegateInfo 数据结构

**问题**：怎么存"含 type-param 的 delegate 签名"？

**决定**：`DelegateInfo` 携带：
- `Signature: Z42FuncType` —— 用 `Z42GenericParamType("T")` 等占位填充
- `TypeParams: List<string>` —— 与 Signature 中占位名对应

实例化时按 type-args 走 `SubstituteTypeParams` 已有路径（与 generic class / func 复用）—— 不引入新机制。

### Decision 3: stdlib 文件位置 & 命名

**问题**：放哪里？

**决定**：`src/libraries/z42.core/src/Delegates.z42`。理由：
- `z42.core` 是 stdlib 根；现有 `Object.z42` / `Exception.z42` 等同级
- `Delegates.z42` 单文件包所有 delegate 类型；未来 N>4 自动生成时也写到此文件
- 命名与现有 `Convert.z42` / `Range.z42` 风格一致（功能聚类）

### Decision 4: arity 范围

**问题**：手写到几 arity？

**决定**：**0, 1, 2, 3, 4**（共 5 阶 × 2 [Action+Func] + 1 [Predicate-T-only] = 11 个 delegate）。

理由：
1. 0-4 覆盖 95%+ 真实场景（按 C# 经验）
2. stdlib 文件不至于过长（~50 行），人眼可审
3. 5+ 等真实需求或 z42 自举完成后用脚本生成（`tools/gen-delegates.z42` per `delegates-events.md` §3.4）

### Decision 5: Predicate 的覆盖

**决定**：仅 `Predicate<T>` 单 arity（与 C# 一致）。

C# `Predicate<T>` 也只有 1 arity。多 arity predicate 用户用 `Func<T1, T2, bool>` 表达。

### Decision 6: 不实现 N arity 自动生成脚本

`docs/design/delegates-events.md` §3.4 提到 `tools/gen-delegates.z42` 自动生成 16 arity。**v1 不实施**。

理由：
- z42 当前未自举（编译器是 C#），跑 z42 脚本生成 z42 源码是循环依赖
- 5+ arity 需求未现，不阻塞 D2 推进
- 自举后用 `Source Generator` 或类似机制做（独立 spec）

## Implementation Notes

### Parser 侧

`ParseDelegateDecl` 解除限制：

```csharp
private static DelegateDecl ParseDelegateDecl(
    ref TokenCursor cursor, Visibility vis, Span startSpan)
{
    ExpectKind(ref cursor, TokenKind.Delegate);
    var retType = TypeParser.Parse(cursor).Unwrap(ref cursor);
    var name    = ExpectKind(ref cursor, TokenKind.Identifier).Text;

    // D1c: 解析 <T1, T2, ...> 类型参数列表（可选）
    List<string>? typeParams = null;
    if (cursor.Current.Kind == TokenKind.Lt)
    {
        typeParams = ParseTypeParamList(ref cursor);  // 复用现有 helper
    }

    ExpectKind(ref cursor, TokenKind.LParen);
    var parms = ParseParamList(ref cursor);
    ExpectKind(ref cursor, TokenKind.RParen);
    ExpectKind(ref cursor, TokenKind.Semicolon);
    return new DelegateDecl(name, vis, parms, retType,
        startSpan.Merge(cursor.Current.Span), typeParams);
}
```

### SymbolCollector 侧

```csharp
foreach (var d in cu.Delegates)
{
    // 设置 active type params 让 ResolveType 把 T → Z42GenericParamType("T")
    var prev = _activeTypeParams;
    _activeTypeParams = d.TypeParams != null
        ? new HashSet<string>(d.TypeParams)
        : null;
    try
    {
        var paramTypes = d.Params.Select(p => ResolveType(p.Type)).ToList();
        var retType    = ResolveType(d.ReturnType);
        var sig        = new Z42FuncType(paramTypes, retType);
        delegates[d.Name] = new DelegateInfo(sig,
            d.TypeParams ?? Array.Empty<string>());
    }
    finally { _activeTypeParams = prev; }
}
```

`ResolveType` GenericType 路径增加 delegate 实例化分支：

```csharp
case GenericType gt when delegates.TryGetValue(gt.Name, out var info):
{
    if (gt.TypeArgs.Count != info.TypeParams.Count)
        Error(...);
    var subMap = info.TypeParams
        .Zip(gt.TypeArgs.Select(ResolveType), (k, v) => (k, v))
        .ToDictionary(x => x.k, x => x.v);
    return SubstituteTypeParams(info.Signature, subMap);
}
```

**移除** line 211 (`"Action"` 无 type args 分支) + line 248-253 (`"Func"` / `"Action"` GenericType 分支)。

### stdlib `Delegates.z42`

```z42
namespace Std;

// === Action (void return) ===
public delegate void Action();
public delegate void Action<T>(T arg);
public delegate void Action<T1, T2>(T1 a, T2 b);
public delegate void Action<T1, T2, T3>(T1 a, T2 b, T3 c);
public delegate void Action<T1, T2, T3, T4>(T1 a, T2 b, T3 c, T4 d);

// === Func (with return) ===
public delegate R Func<T, R>(T arg);
public delegate R Func<T1, T2, R>(T1 a, T2 b);
public delegate R Func<T1, T2, T3, R>(T1 a, T2 b, T3 c);
public delegate R Func<T1, T2, T3, T4, R>(T1 a, T2 b, T3 c, T4 d);

// === Predicate (bool return) ===
public delegate bool Predicate<T>(T arg);
```

**注意命名冲突**：`Action<T>` 在 generic 1-arity 版本用名字 `Action`（z42 现有重载机制是否支持泛型 arity 重载？）—— C# 用 `Action<T>` vs `Action<T1,T2>` 是同名不同 arity 实例化的方式。z42 的 SymbolTable 应该按 `name + arity` 存：`Delegates["Action"]` 是 `List<DelegateInfo>` 或者 `Dictionary<int arity, DelegateInfo>`。

> 实施时如果发现 z42 现有 generic 机制不支持同名多 arity，**停下与 User 讨论**（违反 spec 6.5 决策点）—— 走"按 arity suffix 命名"或"多 DelegateInfo 列表"两个方案。

## Testing Strategy

### 单元测试

`src/compiler/z42.Tests/GenericDelegateTests.cs`（NEW）：

1. `Generic_Delegate_Parses_With_TypeParams` — `delegate R Func<T,R>(T arg);` 产生正确 AST
2. `Generic_Delegate_Resolves_Instantiation` — `Func<int, string>` → Z42FuncType([int], string)
3. `Multiple_Arity_Same_Name` — `Action<int>` 和 `Action<int, int>` 互不冲突
4. `Wrong_Arity_Reports_Error` — `Func<int>` 缺 R 报错
5. `Hardcoded_Action_Func_Removed` — SymbolCollector 不再有写死分支（grep 验证）

`src/compiler/z42.Tests/PredicateTests.cs`（NEW）：

6. `Predicate_Resolves` — `Predicate<int>` → Z42FuncType([int], bool)
7. `Predicate_Lambda_Assignment` — `Predicate<int> isPositive = (int x) => x > 0;`

### Golden test

`src/runtime/tests/golden/run/delegate_d1c_generic/source.z42`：

```z42
namespace Demo;
using Std;
using Std.IO;

void Greet(int x) { Console.WriteLine($"hi {x}"); }

void Main() {
    // Action 0-arity
    Action done = () => Console.WriteLine("done");
    done();

    // Action<T>
    Action<int> a1 = Greet;
    a1(7);

    // Func<T, R>
    Func<int, int> sq = (int x) => x * x;
    Console.WriteLine(sq(5));

    // Func<T1, T2, R>
    Func<int, int, int> add = (int a, int b) => a + b;
    Console.WriteLine(add(3, 4));

    // Predicate
    Predicate<int> isEven = (int x) => x % 2 == 0;
    Console.WriteLine(isEven(4));
    Console.WriteLine(isEven(5));
}
```

期望输出：
```
done
hi 7
25
7
true
false
```

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # +7
./scripts/regen-golden-tests.sh
./scripts/test-vm.sh                                   # +1×2 modes
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| 删除 hardcoded `Action`/`Func` desugar 后现有测试失败 | 实施前确认 stdlib delegate 加载到 SymbolTable.Delegates 路径完整；测试源 `using Std;` 显式 import |
| 同名多 arity（`Action<T>` vs `Action<T1,T2>`）冲突 | 见 design Implementation Notes 末尾 —— 实施时检查现有机制；不支持则停下讨论 |
| stdlib delegate 与 SymbolCollector 已有的 `_activeTypeParams` 路径耦合 | 复用现有路径（generic class / func 同款）；测试覆盖 |
| zbc 元数据无 delegate type info | 与 generic class 共享 type-param 通道（design Decision 6 in proposal）；不引入新 section |
| `Predicate` 名称与 stdlib 已有同名类型冲突 | grep 确认无现有 Predicate 定义（应该没有，因为之前完全缺失）|
