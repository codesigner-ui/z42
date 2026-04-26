# Design: Generic Interface Dispatch Fix

## Architecture

```
Before                                  After
──────                                  ─────
Z42InterfaceType                        Z42InterfaceType
  Name                                    Name
  Methods (含 generic param T)            Methods (同)
  TypeArgs?      ← 无对应 TypeParams      TypeArgs?
  StaticMembers?                          StaticMembers?
                                         + TypeParams?  ← 新增

ResolveType `IEquatable<int>`           ResolveType `IEquatable<int>`
  → Z42InterfaceType                      → Z42InterfaceType
    .TypeArgs = [int]                      .TypeArgs = [int]
    .Methods = { Equals: T→bool }          .TypeParams = ["T"]      ← 新
                                          .Methods = { Equals: T→bool }

method dispatch (e.Equals(v)):          method dispatch (e.Equals(v)):
  CheckArgTypes(args, [T])                subMap = { T → int }
    ↓ T vs int → ❌                        imtSub = SubstituteTypeParams(imt, subMap)
                                          CheckArgTypes(args, imtSub.Params=[int])
                                            ↓ int vs int → ✓
```

## Decisions

### Decision 1: 加 TypeParams 字段而非新建 InstantiatedInterfaceType

**问题**：参考 `Z42InstantiatedType`（class instantiated）的设计，是否给
interface 也建一个 `Z42InstantiatedInterfaceType`？

**决定**：**加字段**到 Z42InterfaceType。

**理由**：
- 接口比类简单，只有 Methods 字典，没有 fields / static / inheritance chain
- Z42InstantiatedType 是因为 class 有 def/inst 双层结构（def 持基类、字段、方法等），inst 持 TypeArgs，需要双 record
- 接口字段少，加 TypeParams 后 substituted view 直接由 helper 计算，不必双 record
- 现有所有 `Z42InterfaceType` 用法（5+ 处）只需补字段，不必同步切换两个 record

### Decision 2: TypeParams 作为新增 nullable 字段

**问题**：所有现有 `new Z42InterfaceType(...)` 调用是否要改？

**决定**：新增字段放末尾 nullable，C# record `with` 语法 / positional 调用
保持兼容。但**所有泛型接口构造路径**必须显式传 TypeParams（否则 dispatch
broken）。非泛型接口（IDisposable 等）传 null 即可。

代码改动点（构造泛型接口的所有位置）：
1. `ImportedSymbolLoader.BuildInterfaceSkeleton` — 从 ExportedInterfaceDef.TypeParams
2. `ImportedSymbolLoader.FillInterfaceMembersInPlace` — 同上
3. `SymbolCollector.CollectInterfaces` — 从 InterfaceDecl.TypeParams（local interface）
4. `SymbolCollector.ResolveType` GenericType 分支 — 实例化时**保留 def 的 TypeParams**
5. `TypeChecker.SubstituteInterfaceTypeArgs` — substituted 后保留 TypeParams

### Decision 3: BuildInterfaceSubstitutionMap helper

```csharp
private static IReadOnlyDictionary<string, Z42Type>?
BuildInterfaceSubstitutionMap(Z42InterfaceType iface)
{
    if (iface.TypeParams is null || iface.TypeArgs is null) return null;
    if (iface.TypeParams.Count != iface.TypeArgs.Count) return null;
    var map = new Dictionary<string, Z42Type>();
    for (int i = 0; i < iface.TypeParams.Count; i++)
        map[iface.TypeParams[i]] = iface.TypeArgs[i];
    return map;
}
```

返回 null 时 caller 跳过 substitution（非泛型接口或无 TypeArgs 实例）。

### Decision 4: TypeChecker.Calls Z42InterfaceType 分支改造

```csharp
if (recvExpr.Type is Z42InterfaceType ifaceType)
{
    if (ifaceType.Methods.TryGetValue(mCallee.Member, out var imt))
    {
        var subMap = BuildInterfaceSubstitutionMap(ifaceType);
        var imtSub = subMap is null ? imt : (Z42FuncType)SubstituteTypeParams(imt, subMap);
        CheckArgCount(argBound.Count, imtSub.MinArgCount, imtSub.Params.Count, call.Span);
        CheckArgTypes(call.Args, argBound, imtSub.Params);
        return new BoundCall(BoundCallKind.Virtual, recvExpr, ifaceType.Name,
            mCallee.Member, null, argBound, imtSub.Ret, call.Span);
    }
    ...
}
```

### Decision 5: BindMemberExpr Z42InterfaceType auto-property dispatch

```csharp
if (target.Type is Z42InterfaceType ifaceType)
{
    if (ifaceType.Methods.TryGetValue($"get_{m.Member}", out var ifaceGetter)
        && ifaceGetter.Params.Count == 0)
    {
        var subMap = BuildInterfaceSubstitutionMap(ifaceType);
        var subRet = subMap is null ? ifaceGetter.Ret
                                    : SubstituteTypeParams(ifaceGetter.Ret, subMap);
        return new BoundCall(BoundCallKind.Virtual, target, ifaceType.Name,
            $"get_{m.Member}", null, new List<BoundExpr>(), subRet, m.Span);
    }
    if (ifaceType.Methods.TryGetValue(m.Member, out var ifmt))
        return new BoundMember(target, m.Member, ifmt, m.Span); // 普通方法引用，不 substitute
}
```

普通方法引用（不调用，仅取 method type）保持 generic param 形式 — 调用时
通过 BindCall 路径再做 substitute。

### Decision 6: RequireAssignable ClassType→InterfaceType TypeArgs-aware

```csharp
if (target is Z42InterfaceType targetIface && source is Z42ClassType sourceImplCt)
{
    // 已有 ImplementedInterfacesByName 枚举所有声明 + InterfacesEqual 比较 TypeArgs
    foreach (var declared in _symbols.ImplementedInterfacesByName(sourceImplCt.Name, targetIface.Name))
    {
        if (InterfacesEqual(declared, targetIface)) return; // 兼容
    }
    // 老路径：name-only 兼容（仅当 target 无 TypeArgs，向后兼容非泛型）
    if (targetIface.TypeArgs is null
        && _symbols.ImplementsInterface(sourceImplCt.Name, targetIface.Name))
        return;
}
```

利用现有 `ImplementedInterfacesByName` + `InterfacesEqual`（已能比较
TypeArgs）；只在 target.TypeArgs 为 null 时退化为 name-only。

### Decision 7: 不动 IsAssignableTo

`Z42Type.cs IsAssignableTo` 是基础类型兼容检查，不知道 SymbolTable。继续走
`RequireAssignable` 的 ClassType↔InterfaceType 特殊分支（Decision 6）。
按 spec D5 of #3 同样原则：根因在 `RequireAssignable` 的 InterfaceType 分支
缺 TypeArgs 比较，修这里；不在 `IsAssignableTo` 加桥接。

### Decision 8: Z42InstantiatedType 接口实现走相同路径

`Z42InstantiatedType` 实例（如 `MyList<int>`）赋值给 `IEnumerable<int>`：
RequireAssignable 同样需要识别。复用 ImplementedInterfacesByName + 类
substitution map 应用到 declared interfaces。

```csharp
if (target is Z42InterfaceType targetIface && source is Z42InstantiatedType sourceInst)
{
    var classSub = BuildSubstitutionMap(sourceInst);
    foreach (var declared in _symbols.ImplementedInterfacesByName(
        sourceInst.Definition.Name, targetIface.Name))
    {
        var declaredSub = SubstituteInterfaceTypeArgs(declared,
            classSub.ToDictionary(kv => kv.Key, kv => kv.Value));
        if (InterfacesEqual(declaredSub, targetIface)) return;
    }
}
```

## Implementation Notes

### 关键不变量

- 所有泛型接口的 Z42InterfaceType 实例必带 TypeParams（除非临时占位骨架）
- BuildInterfaceSubstitutionMap 在 TypeParams.Count != TypeArgs.Count 时返回 null（不 panic）
- SubstituteTypeParams（已存在）能处理 Z42FuncType（递归 Params + Ret）

### 测试设计

新增 `run/101_generic_interface_dispatch`：

```z42
class MyInt : IEquatable<int> {
    public int V;
    MyInt(int v) { this.V = v; }
    bool Equals(int other) { return this.V == other; }
    int GetHashCode() { return this.V; }
}

class IntDescComparer : IComparer<int> {
    int Compare(int x, int y) { return y - x; }
}

bool Check(IEquatable<int> e, int v) {
    return e.Equals(v);
}

int CmpThrough(IComparer<int> c, int a, int b) {
    return c.Compare(a, b);
}

void Main() {
    var m = new MyInt(42);
    Console.WriteLine(Check(m, 42));        // True
    Console.WriteLine(Check(m, 99));        // False

    var c = new IntDescComparer();
    Console.WriteLine(CmpThrough(c, 3, 5)); // 2
    Console.WriteLine(CmpThrough(c, 5, 3)); // -2
}
```

### 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| 现有 Z42InterfaceType 构造点漏改 TypeParams | 中 | grep `new Z42InterfaceType` 全部检查；非泛型接口 null 即可 |
| L3-G3a/G3d 约束 zbc 序列化反序列化（含 InterfaceConstraints） | 低 | TypeParams 是 TypeChecker 内部字段，不参与 zbc 序列化 |
| L3-G2.5 chain (TypeArgs 比较) | 低 | InterfacesEqual 已正确比较 TypeArgs；本变更不动它 |
| Z42InstantiatedType + interface assign 现有路径 | 中 | Decision 8 显式覆盖；测试用 `MyList<int> → IEnumerable<int>` 边界 |

## Testing Strategy

- 新增 `run/101_generic_interface_dispatch` golden test 端到端验证
- 全量回归 dotnet test / test-vm.sh / cargo test
- 关键 review check：grep 确认所有 `new Z42InterfaceType(...)` 调用都被审视
