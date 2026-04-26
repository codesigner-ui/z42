# Proposal: 修复 generic interface method dispatch — TypeArgs 在 method args 上正确 substitute

## Why

Wave 3 实施时发现：用户代码经接口变量调用泛型接口方法不工作。

```z42
class MyInt : IEquatable<int> { bool Equals(int other) { ... } ... }
bool Check(IEquatable<int> e, int v) { return e.Equals(v); }   // ← 报错
```

错误：`argument 1: expected T, got int`。

**根因定位**（按 [.claude/rules/workflow.md "修复必须从根因出发"](.claude/rules/workflow.md#修复必须从根因出发-2026-04-26-强化)）：

- [Z42InterfaceType](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs#L186-L194) 数据结构含 `TypeArgs`（实例化时的具体类型 `[int]`），但**没有 `TypeParams`**（接口声明的类型参数名 `["T"]`）
- ImportedSymbolLoader 加载 stdlib 接口时，`Methods` 字典里的方法签名是 generic param 形式（`bool Equals(T other)`，T 是 `Z42GenericParamType("T")`），未保存对应 TypeParams 到 Z42InterfaceType
- [TypeChecker.Calls.cs:183-196](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs#L183-L196) 的 Z42InterfaceType 分支调用 `CheckArgTypes(call.Args, argBound, imt.Params)` 直接用 generic param T，**未基于 TypeArgs 做 substitution** → 比较 T vs int 报错

**对照**：[BindMemberExpr](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs#L545-L571) 在 `Z42InstantiatedType`（class instantiated）路径有 `BuildSubstitutionMap(inst)` + `SubstituteTypeParams` 完整处理；但 `Z42InterfaceType` 缺这个对称机制，因为 InterfaceType 缺 TypeParams 字段，无法配对 TypeArgs 建 map。

## What Changes

### 1. Z42InterfaceType 加 TypeParams 字段

```csharp
public sealed record Z42InterfaceType(
    string Name,
    IReadOnlyDictionary<string, Z42FuncType> Methods,
    IReadOnlyList<Z42Type>? TypeArgs = null,
    IReadOnlyDictionary<string, Z42StaticMember>? StaticMembers = null,
    /// 新增：接口声明的类型参数名（"T", "K, V" 等）。用于把 Methods 字典里
    /// generic param T 的方法签名按 TypeArgs 替换为具体类型，支持
    /// `IEquatable<int>` 实例上的 method dispatch.
    IReadOnlyList<string>? TypeParams = null);
```

### 2. 所有 Z42InterfaceType 构造点填 TypeParams

- [ImportedSymbolLoader.BuildInterfaceSkeleton / FillInterfaceMembersInPlace](src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs)
- [SymbolCollector.CollectInterfaces](src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs#L141)
- [SymbolCollector.ResolveType GenericType 分支](src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs#L218)（保留 def TypeParams + 新 TypeArgs）
- 其他 `new Z42InterfaceType(...)` 散点

### 3. TypeChecker.Calls.cs Z42InterfaceType 分支加 substitution

```csharp
if (recvExpr.Type is Z42InterfaceType ifaceType)
{
    if (ifaceType.Methods.TryGetValue(mCallee.Member, out var imt))
    {
        // 新：build subMap from TypeParams ↔ TypeArgs，substitute method sig
        var subMap = BuildInterfaceSubstitutionMap(ifaceType);
        var imtSub = (Z42FuncType)SubstituteTypeParams(imt, subMap);
        CheckArgCount(...);
        CheckArgTypes(call.Args, argBound, imtSub.Params);
        return new BoundCall(..., imtSub.Ret, ...);
    }
}
```

### 4. BindMemberExpr Z42InterfaceType property dispatch 同步

[BindMemberExpr](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs#L598) auto-property getter 路径也要 substitute return type。

### 5. RequireAssignable: ClassType→InterfaceType 兼容性 with TypeArgs

[Z42Type.cs IsAssignableTo](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs#L34-L88) 当前不在 ClassType→InterfaceType 路径检查 TypeArgs；
[TypeChecker.RequireAssignable:934-935](src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs#L934-L935) 仅 `ImplementsInterface(name, name)` name-only。需要：

```csharp
if (target is Z42InterfaceType targetIface && source is Z42ClassType sourceImplCt
    && _symbols.ImplementedInterfacesByName(sourceImplCt.Name, targetIface.Name).Any(declared =>
        InterfacesEqual(declared, targetIface)))
    return;
```

利用现有 `ImplementedInterfacesByName` + `InterfacesEqual`（已能比较 TypeArgs）。

## Scope

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | edit | Z42InterfaceType 加 TypeParams 字段 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` | edit | CollectInterfaces / ResolveType 填 TypeParams |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | edit | BuildInterfaceSkeleton / FillInterfaceMembersInPlace 填 TypeParams |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | edit | RequireAssignable 用 TypeArgs-aware 比较；新增 BuildInterfaceSubstitutionMap helper |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs` | edit | Z42InterfaceType 分支加 substitution |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | edit | BindMemberExpr Z42InterfaceType 分支同步 substitute |
| `src/runtime/tests/golden/run/101_generic_interface_dispatch/` | add | 用户类实现 `IEquatable<int>` / `IComparer<int>`，经接口变量调用 |
| `docs/design/compiler-architecture.md` | edit | TypeChecker 章节补"Z42InterfaceType TypeParams"说明 |

## Out of Scope

- IComparable 的 same-type substitution 已工作（通过 Z42GenericParamType 在 generic class context），不动
- 接口继承（`interface IDerived<T> : IBase<T>`）的方法继承 — 现有 z42 不支持接口继承（无 base interface methods 拷贝），不在本变更
- Variance（`IEnumerable<out T>` 协变）— L3+ 范围

## Open Questions

- [x] Z42InterfaceType 字段顺序 — 加在末尾（向后兼容现有 `with { ... }` 用法）
- [x] 是否需要新建 `Z42InstantiatedInterfaceType`（类似 Z42InstantiatedType）— **不需要**，加字段更简洁，无需双 record
- [x] InterfaceEqual 是否要考虑 TypeParams 不同的同名接口 — **不需要**，TypeParams 由声明方决定，同名接口必同 TypeParams

## Blocks / Unblocks

- **Unblocks**：
  - Wave 3 接口契约真正可用（IComparer<int> / IEqualityComparer<T> / IEnumerable<T> 经接口变量调用）
  - 未来 Sort / Dictionary 重载接受 IComparer / IEqualityComparer 实参
  - LINQ 风格扩展依赖 IEnumerable<T> 接口能正常 dispatch
- **Blocks**：无
