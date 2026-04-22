# Design: 裸类型参数约束

## Architecture

```
 Source         TypeChecker                       IrGen/zbc/VM
 ──────────────────────────────────────────────────────────────────────
 where U: T     GenericConstraintBundle           ConstraintBundle
                + TypeParamConstraint: "T"        + type_param_constraint
                     (name of the other tp)           : Option<String>

 Body           BindMember/BindCall 分支          (no IR change; still VCall)
 u.Method():      if U has TypeParamConstraint T,
                    fallback to T's bundle

 Call-site      ValidateGenericConstraints 扩展   (loader pass-through)
 F(a, d):         IsSubclassOf(typeArg[U], typeArg[T])
```

## Decisions

### Decision 1: 约束载体 — 单独字段 vs 复用 Z42GenericParamType

**问题**：T 已经是 `Z42GenericParamType`，约束"U: T"能不能用 Z42GenericParamType("T") 塞进 `BaseClassConstraint`？

**选项**：
- A. 新字段 `TypeParamConstraint: string?`（只存 T 的名字）
- B. `BaseClassConstraint: Z42Type?` 改为 union，收 class 或 generic-param
- C. 让 U 的 `InterfaceConstraints` 收 Z42GenericParamType

**决定**：A。
- 语义清晰（基类/接口/flag/裸 typeparam 四种约束各自独立字段）
- 零破坏现有 `BaseClassConstraint` / `InterfaceConstraints` 使用点
- 只存名字避免 Z42GenericParamType 嵌套的循环引用风险
- 回查 T 的 bundle 用名字在同一 decl 的 constraint map 里查

### Decision 2: ResolveWhereConstraints 识别策略

约束 type expression 的 NamedType 恰好是同 decl 的 type param 时 → 识别为 `TypeParamConstraint`。

```csharp
case NamedType nt when declaredTypeParams.Contains(nt.Name):
    if (typeParamConstraint != null)
        error("generic parameter cannot have multiple type-param constraints");
    typeParamConstraint = nt.Name;
    break;
// 否则继续原有 class / interface 解析
```

放在 class/interface 判断之前（因 `declaredTypeParams` 里的名字 `ResolveType` 会返回
Z42GenericParamType，顺手吃掉会走 default 错误分支）。

### Decision 3: 成员查找的递归

`u.Method()` where U: T：
1. 先查 U 的 `InterfaceConstraints` / `BaseClassConstraint`（若有）
2. 若未命中，且 `U.TypeParamConstraint == T`，跳到 T 的 bundle，重复 1 次
3. 若 T 的 bundle 也指向另一 type param（`T.TypeParamConstraint == V`）→ **不再递归**（防环 + 实际少见），报错

实现：BindMemberExpr / BindCall 里把"跳转 1 次"做成 helper：

```csharp
GenericConstraintBundle EffectiveBundle(Z42GenericParamType gp)
{
    var b = bundle(gp.Name);
    if (!HasMember(b, memberName) && b.TypeParamConstraint is { } t)
    {
        var b2 = bundle(t);  // 一跳
        return MergeBundles(b, b2);  // 仅用于查找，不改存储
    }
    return b;
}
```

### Decision 4: 调用点校验的 IsSubclassOf

所有 typeArg 推断完成后：

```csharp
foreach (tp, bundle in constraints)
{
    if (bundle.TypeParamConstraint is { } otherTp)
    {
        var actualU = typeArgs[tp];
        var actualT = typeArgs[otherTp];
        if (!TypeSatisfiesClassConstraint(actualU, actualT as Z42ClassType)
            // actualT 可能是 Z42ClassType/PrimType/Interface/Option
            // 对非 class 类型，用相等性回退
            && actualU != actualT)
            error(...);
    }
}
```

若 `actualT` 非 class，就只支持相等（避免 primitive 子类型的复杂性）。

### Decision 5: zbc 格式版本 0.5 → 0.6

新字段位 flag bit3 + 条件 u32。与 L3-G3a 同策略（bump 版本 + 全量重生成）：
- stdlib `.zpkg` 重编
- golden `source.zbc` regen

老 zbc（无此字段）不兼容，Reader 严格匹配版本。

### Decision 6: verify_constraints 对裸 type-param 放行

`type_param_constraint` 引用的永远是同一 decl 的 type param，本地可解。Rust VM `verify_constraints` 不做名字校验（类似 interface 放行逻辑）。

## Implementation Notes

### Z42Type.cs

```csharp
public sealed record GenericConstraintBundle(
    Z42ClassType? BaseClass,
    IReadOnlyList<Z42InterfaceType> Interfaces,
    bool RequiresClass = false,
    bool RequiresStruct = false,
    string? TypeParamConstraint = null)
{
    public bool IsEmpty => BaseClass is null && Interfaces.Count == 0
                           && !RequiresClass && !RequiresStruct
                           && TypeParamConstraint is null;
}
```

### ResolveWhereConstraints

```csharp
foreach (var tx in entry.Constraints)
{
    if (tx is NamedType nt && declaredTypeParams.Contains(nt.Name))
    {
        if (typeParamConstraint != null)
            _diags.Error(..., "generic parameter cannot have multiple type-param constraints");
        typeParamConstraint = nt.Name;
        continue;
    }
    // 原有分派 class / interface / 报错
}
```

### ValidateGenericConstraints 扩展

```csharp
// 收集所有 typeArg 映射（declName 的 typeParams 顺序 → 推断出的实参）
var typeArgMap = BuildTypeArgMap(typeParams, typeArgs);
foreach (var (tp, bundle) in constraints)
{
    if (bundle.TypeParamConstraint is { } otherTp
        && typeArgMap.TryGetValue(tp, out var uArg)
        && typeArgMap.TryGetValue(otherTp, out var tArg))
    {
        if (!TypeArgSubsumedBy(uArg, tArg))
            _diags.Error(..., "type argument `{uArg}` for `{tp}` is not a subtype of `{tArg}` (required by `{tp}: {otherTp}`)");
    }
}

static bool TypeArgSubsumedBy(Z42Type sub, Z42Type sup) => (sub, sup) switch
{
    _ when sub == sup => true,
    (Z42ClassType cs, Z42ClassType cp) => cs.Name == cp.Name || _symbols.IsSubclassOf(cs.Name, cp.Name),
    _ => false,  // primitive/interface 间只支持相等
};
```

## Testing Strategy

### 单元测试（TypeCheckerTests.cs）

```
TC22 Generic_BareTypeParam_SubclassArg_Ok        — F<Animal, Dog> 通过
TC23 Generic_BareTypeParam_SiblingArg_Error      — F<Animal, Vehicle> 报错
TC24 Generic_BareTypeParam_SameArg_Ok            — F<Animal, Animal>
TC25 Generic_BareTypeParam_PlusInterface_Combo   — where U: T + IDisplay
TC26 Generic_BareTypeParam_InClass_ReturnAssign  — T Get(U child) { return child; }
```

### Round-trip（ZbcRoundTripTests.cs）

```
Constraints_BareTypeParam_SurvivesRoundTrip
```

### Golden

```
run/72_generic_bare_typeparam/        — Container<Animal, Dog> 实例化 + Get(child)
errors/33_bare_typeparam_not_subtype  — F<Animal, Vehicle>(a, v)
```

### 验证门
- `dotnet build` + `cargo build` 无错误
- `dotnet test` 全绿（500 + 5 TC + 1 round-trip + 1 golden + 1 error golden = 508）
- `cargo test --lib` 新增 1 constraint test
- `./scripts/test-vm.sh` 全绿（136 → 137）
- stdlib + golden 全量重生成（zbc 0.6）

## Risks

| 风险 | 缓解 |
|------|------|
| 识别顺序错：`U: T` 中 T 先被 `ResolveType` 返回 Z42GenericParamType → 原有分派报错 | 在 class/interface 分派前，先用 `NamedType.Name ∈ declaredTypeParams` 短路判定 |
| 成员查找递归引发无限循环 | 限制"一跳"策略，显式不支持链式 |
| 推断出的 `actualT` 是 primitive / interface 时 IsSubclassOf 不适用 | 回退为相等性判断（保守） |
| zbc 版本 0.6 + 全量重编工作量 | 与 L3-G3a 同策略；规范 regen 流程 |
