# Design: Z42Type record 结构 equality

## Architecture

```
共有 pattern：
  record (sealed)
  ├── 标量 / 引用字段        ← record 默认 Equals OK
  └── IReadOnlyList<Z42Type>   ← 默认引用比较，BUG 源
                                  ▼
                             Equals override：
                             逐项 elem.Equals(elem') 递归（Z42Type 自身 Equals）
                                  ▼
                             GetHashCode override：
                             组合标量字段 hash + ∑ list 元素 hash

应用到三个 record：
  Z42InstantiatedType  → TypeArgs (List<Z42Type>)
  Z42InterfaceType     → TypeArgs (List<Z42Type>?) + TypeParams (List<string>?)
  Z42FuncType          → Params (List<Z42Type>)
```

## Decisions

### Decision 1: override Equals/GetHashCode vs 类型 intern table
**问题：** 怎么让结构相等的 record 比较为相等？
**选项：**
- A. **Equals/GetHashCode override**：record 的 equality 走深度比较
- B. **Intern table**：构造工厂走 cache，相同结构返回同对象，引用比较即结构比较

**决定：A。** 原因：
- A 直观、与 C# record 语义对齐；调用方零改动
- B 需要全局 cache + 线程安全 + 全代码库审查所有 `new Z42*` 入口（多处直接 new），改动面大
- z42 当前规模 intern 收益不明确；A 不阻拦 B 后续追加

### Decision 2: 递归 `Z42Type.Equals` 而非抽 `TypeArgEquals` helper
**问题：** [TypeChecker.Generics.cs:232-249](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs#L232-L249) 已有 `TypeArgEquals`，复用还是重写？
**选项：**
- A. 抽 `TypeArgEquals` 成 `Z42Type` 静态 helper
- B. 在 record.Equals 内直接 element-wise + `elem.Equals(other_elem)` 递归

**决定：B。** 原因：
- 单个 `Z42Type` 子类型作为 record 自身的 Equals 没问题（不持 list-of-Z42Type）；递归到本类型时调用本 override 路径
- 不引入 cross-file 依赖（Z42Type.cs ↔ TypeChecker.Generics.cs）
- TypeArgEquals 旧实现是否完全等价于"原始 record Equals + element-wise"未审计；用最朴素递归更安全

实现 pattern（三个 record 共用）：
```csharp
public sealed record Z42InstantiatedType(...) : Z42Type {
    public bool Equals(Z42InstantiatedType? other) {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (!ReferenceEquals(Definition, other.Definition)
            && !Definition.Equals(other.Definition)) return false;
        return ListEquals(TypeArgs, other.TypeArgs);
    }
    public override int GetHashCode() {
        var hc = new HashCode();
        hc.Add(Definition);
        foreach (var t in TypeArgs) hc.Add(t);
        return hc.ToHashCode();
    }
}
```

`ListEquals` —— 静态助手放 `Z42Type` 抽象基类，签名 `static bool ListEquals<T>(IReadOnlyList<T>?, IReadOnlyList<T>?)`，null safe + count + element-wise `EqualityComparer<T>.Default.Equals`。

### Decision 3: Z42InterfaceType.Methods 字典字段不深比
**问题：** Z42InterfaceType 还有 `IReadOnlyDictionary<string, Z42FuncType> Methods` 字段，是否需要深比？
**决定：** 不深比，依赖默认引用比较。原因：
- 实践中两次构造同一接口实例化时 Methods 字典对象往往是同一引用（来自 ClassType / InterfaceCollector 缓存）
- 字典深比成本高，且 Methods 通常由"interface name 唯一确定"
- Equals 实现里加注释说明这个假设；如未来发现 bug 再 escalate

实施时仅深比 `TypeArgs` + `TypeParams`（list 字段）；`Methods` / `StaticMembers` 走 reference / record 默认。

### Decision 4: TypeChecker.Generics.cs 的 TypeArgEquals 何去何从
**决定：** 保留观察，本 spec 不动。它可能用于不通过 record Equals 的 ad-hoc 比较；归档备注记 follow-up。

### Decision 5: IsAssignableTo 中现有 workaround 是否删除
**决定：** **保留**。原因：
- 防御性 —— 即使 Equals 修好，line 45 `target == source` 走 record `==` 现在也工作
- workaround 路径还兼顾**类型不完全相等但相互可赋值**的情形（例如 IsAssignableTo 的 element-wise 递归调用 `IsAssignableTo` 而非 `Equals`，允许子类型 / 数值放宽）
- 删除是独立 cleanup，本 spec 不混入

## Implementation Notes

- C# record "自定义 Equals" 必须同时定义 `Equals(MyType?)` (strongly-typed) + `GetHashCode`；`Equals(object?)` 与 `==` `!=` 由 record 自动生成派发到 strongly-typed
- `ReferenceEquals(Definition, ...)` 假定 ClassType 全局唯一；fallback 调用 `Definition.Equals(...)` 防御性兜底
- `Z42InterfaceType.TypeArgs` 是 nullable list；`ListEquals` 处理 null（两端 null 相等；一端 null 不等）
- `Z42InterfaceType.TypeParams` 是 `IReadOnlyList<string>?`；string 默认 Equals 已正确，`ListEquals<string>` 用 `EqualityComparer<string>.Default`

## Testing Strategy

- 单元测试 `Z42TypeEqualityTests.cs`：12+ scenarios 覆盖三个 record（spec 列出）
- D2b 阻塞用例（typecheck 层）：构造 Z42InterfaceType 对断言 IsAssignableTo true
- dotnet test 全绿（基线 +12）
- VM 不受影响
- stdlib build 不退化（D2b 解封需 Spec 3）
