# Design: L3-G2.5 基类约束

## Architecture

```
 Source                 AST                 TypeCheck                      Codegen/VM
 ──────────────────────────────────────────────────────────────────────────────────
 where T: Animal        GenericConstraint   Z42GenericParamType            (no change)
 where T: Animal+IFoo   TypeParam="T"       Name="T"
                        Constraints=[       Constraints: IList<Z42Type>    VCall /
                          NamedType("Animal"),   ├── Z42ClassType          FieldGet
                          NamedType("IFoo")      └── Z42InterfaceType
                        ]
```

## Decisions

### Decision 1: 约束类型的统一表示

**问题**：`Z42GenericParamType.Constraints` 目前是 `List<Z42InterfaceType>`。基类也要容纳进来。

**选项**：
- A. 合并为 `List<Z42Type>`，运行时判断是 Interface 还是 Class
- B. 双字段：`Interfaces: List<Z42InterfaceType>` + `BaseClass: Z42ClassType?`
- C. 自定义 union 类型 `ConstraintEntry`

**决定**：B。
- 单继承语义天然对应"最多一个基类" + "多个接口" → 双字段比 list 更精确
- 校验 "基类只能一个 / 只能首位" 在类型层面就能表达
- 后续 L3-G2.5 小迭代添加 `new()` / `class` / `struct` 等 flag 时，扩展为多字段更清晰（每个约束一个字段）
- 与 C# Z42GenericParamType 记录结构对齐

### Decision 2: 基类约束的语法位置

**问题**：`where T: Base + I` 中基类位置？

**选项**：
- A. 必须首位（C# 惯例）
- B. 任意位置，TypeChecker 挑出基类
- C. 独立 keyword / 符号（`where T: Base, T: I`）

**决定**：A。
- 与 C# 开发者习惯一致（上手成本低）
- Parser 不需额外处理，按顺序读；TypeChecker 发现非首位是基类 → 报错
- B 增加 TypeChecker 复杂度和误报风险
- C 破坏熟悉语法

### Decision 3: 方法查找优先级

**问题**：`class Base { void M(){} }` + `interface I { void M(); }` + `where T: Base + I` 上调用 `t.M()` 走哪条？

**决定**：按约束**声明顺序**查找，第一个匹配即返回。
- 基类首位 → 基类方法先命中
- 正确性：基类的 M 通常是具体实现；接口 M 会被基类实现覆盖
- 运行时都走 VCall，最终仍看 T 的实际类型 vtable，语义上等价

### Decision 4: 调用点校验的类满足关系

**问题**：`F<Dog>(...)` 判 Dog 是否满足 `where T: Animal`？

**决定**：复用 `SymbolTable.IsSubclassOf(derived, base)`（已存在，O(1)）。
- `Dog == Animal` 直接通过（同名即满足）
- `Dog` 的祖先集包含 `Animal` → 满足
- 否则 → 报错

不做 interface 到 class 的跨类型推导（nonsensical）。

### Decision 5: `new()` 构造器约束延后

**问题**：`where T: new()` 实现难度？

**分析**：
- 纯编译期语义：要求类型参数有无参 ctor，调用点可检
- 完整语义：允许 `new T()` 在泛型体内实例化 → 需要运行时知道 T 的具体 TypeDesc
- 代码共享策略下，同一份 IR 服务多个 T，`new T()` 无法静态确定 ObjNew 的类名

**决定**：**延后到 L3-G3a**（当 TypeDesc.type_args 填充 + VM 传递实例化信息时一并做）。本迭代不实现。

## Implementation Notes

### TypeChecker: Z42GenericParamType 扩展

```csharp
public sealed record Z42GenericParamType(
    string Name,
    IReadOnlyList<Z42InterfaceType>? InterfaceConstraints = null,
    Z42ClassType? BaseClassConstraint = null) : Z42Type;
```

保留向后兼容：旧代码传 Constraints 的地方全部改名并分流。L3-G2 的 8 个测试用例仍用接口约束，不受影响。

### SymbolTable: 活动约束查询分流

```csharp
// 原：LookupActiveTypeParamConstraints → IReadOnlyList<Z42InterfaceType>?
// 新：
public (Z42ClassType? BaseClass, IReadOnlyList<Z42InterfaceType> Interfaces)
    LookupActiveTypeParamConstraints(string typeParam);
```

`BaseClass` 用于 BindMember/BindCall 优先查找。

### TypeChecker: ResolveWhereConstraints 分流

```csharp
foreach (var tx in entry.Constraints)
{
    var resolved = _symbols.ResolveType(tx);
    if (resolved is Z42ClassType cc)
    {
        if (baseClass != null)
            error("generic parameter {T} cannot have multiple class constraints");
        if (interfaces.Count > 0)
            error("class constraint {cc.Name} must appear first");
        baseClass = cc;
    }
    else if (resolved is Z42InterfaceType iface)
        interfaces.Add(iface);
    else
        error("constraint must be class or interface, got {resolved}");
}
```

### TypeChecker: 方法查找路径

```csharp
// BindMemberExpr / BindCall on receiver of Z42GenericParamType
if (gp.BaseClassConstraint is { } bc)
{
    if (bc.Fields.TryGetValue(member, out var ft))   return FieldAccess(...);
    if (bc.Methods.TryGetValue(member, out var mt))  return VCall(...);
}
foreach (var iface in gp.InterfaceConstraints ?? []) {
    if (iface.Methods.TryGetValue(member, out var mt)) return VCall(...);
}
error(no member `member` on `T` in constraints);
```

### TypeChecker: 调用点校验

`TypeSatisfiesInterface` 重命名为 `TypeSatisfiesConstraint`，分派：

```csharp
bool TypeSatisfiesClassConstraint(Z42Type typeArg, Z42ClassType baseClass) => typeArg switch
{
    Z42ClassType ct => ct.Name == baseClass.Name || _symbols.IsSubclassOf(ct.Name, baseClass.Name),
    Z42GenericParamType g => g.BaseClassConstraint?.Name == baseClass.Name ||
                             (g.BaseClassConstraint != null && _symbols.IsSubclassOf(g.BaseClassConstraint.Name, baseClass.Name)),
    Z42ErrorType or Z42UnknownType => true,
    _ => false,
};
```

### 向后兼容

- L3-G2 的 Constraints: `IReadOnlyList<Z42InterfaceType>?` → 迁移到 `InterfaceConstraints`
- 旧调用 `gp.Constraints` 的代码全量替换（grep 覆盖）
- 新增 `BaseClassConstraint` 初始为 null，不影响现有路径

## Testing Strategy

### 单元测试（TypeCheckerTests.cs）新增

```
TC9:  Generic_BaseClass_FieldAccess_Ok          -- 基类字段
TC10: Generic_BaseClass_MethodCall_Ok           -- 基类方法
TC11: Generic_BaseClassAndInterface_Combo_Ok    -- 基类 + 接口组合
TC12: Generic_CallSite_SubclassSatisfies_Ok     -- F<Dog>
TC13: Generic_CallSite_SiblingClass_Error       -- F<Vehicle> 报错
TC14: Generic_MultipleBaseClasses_Error         -- where T: A + B (两类)
TC15: Generic_BaseClassNotFirst_Error           -- where T: IFoo + Animal
```

### Round-trip 测试

不新增。L3-G2 round-trip 测试（空约束 + 接口约束）仍应通过（验证迁移不破坏）。

### Golden test `71_generic_baseclass`

```z42
class Animal {
    public int legs;
    Animal(int n) { this.legs = n; }
    public virtual string Describe() { return "animal"; }
}

class Dog : Animal {
    Dog() : base(4) { }
    public override string Describe() { return "dog"; }
}

void Introduce<T>(T pet) where T: Animal {
    Console.WriteLine(pet.legs);
    Console.WriteLine(pet.Describe());
}

void Main() {
    Introduce(new Dog());
}

// expected:
// 4
// dog
```

### Error goldens

- `errors/28_generic_non_subclass`：F<Vehicle> 违反 Animal 约束
- （可选）`errors/29_generic_baseclass_not_first`：`where T: IFoo + Animal`

### 验证门

- `dotnet build` + `cargo build` 无错误
- `dotnet test` 100% 通过（475 + 7 = 482）
- `./scripts/test-vm.sh` 100% 通过（134 + 1 = 135）

## Risks

| 风险 | 缓解 |
|------|------|
| 迁移 `Constraints` → `InterfaceConstraints` 遗漏调用点 | grep 全仓引用 + build error 兜底 |
| 基类方法和接口方法同名冲突 | 按声明顺序查找（决策 3）；加 TC 覆盖组合场景 |
| 泛型类字段是 T 类型（`T first;`）+ T 有基类约束 → 字段访问链 | 现有 `LookupActiveTypeParamConstraints` 兜底机制（L3-G2 已做）同样适用 |
