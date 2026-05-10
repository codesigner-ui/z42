# Design: 引用/值类型约束

## Architecture

```
 Source                AST                         TypeCheck                 Codegen/VM
 ─────────────────────────────────────────────────────────────────────────────────────
 where T: class        GenericConstraint(          GenericConstraintBundle   (no change)
 where T: struct+IDisp   TypeParam="T",            + RequiresClass: bool
                         Kinds=[Class/Struct],     + RequiresStruct: bool
                         Constraints=[IDisp]       (验证阶段校验 typeArg)
                       )
```

## Decisions

### Decision 1: AST 表示 — flag 字段 vs 占位 TypeExpr

**问题**：`class` / `struct` 不是类型表达式，放哪？

**选项**：
- A. `GenericConstraint` 新增 `Kinds: GenericConstraintKind`（`[Flags]`）
- B. 用哨兵 `NamedType("class")` 塞进 `Constraints`
- C. 多加两个 bool 字段

**决定**：A。
- 清晰分离类型约束 vs 分类标志
- Parser 读到 `class` / `struct` keyword 直接 flag，不走 TypeParser
- 后续扩展 `new()`、`notnull` 只需加 flag，不破格式

```csharp
[Flags]
public enum GenericConstraintKind { None = 0, Class = 1, Struct = 2 }

public sealed record GenericConstraint(
    string TypeParam,
    List<TypeExpr> Constraints,   // 类 + 接口（与 L3-G2/G2.5 一致）
    Span Span,
    GenericConstraintKind Kinds = GenericConstraintKind.None);
```

### Decision 2: Parser 中 `class` / `struct` 识别

Parser 当前读到 `class` keyword 会当 token `Class`。`struct` 同理。
`ParseWhereClause` 识别：

```csharp
while (true) {
    switch (cursor.Current.Kind) {
        case TokenKind.Class:  kinds |= Class;  cursor = cursor.Advance(); break;
        case TokenKind.Struct: kinds |= Struct; cursor = cursor.Advance(); break;
        default:               types.Add(TypeParser.Parse(cursor).Unwrap(ref cursor)); break;
    }
    if (cursor.Current.Kind != TokenKind.Plus) break;
    cursor = cursor.Advance();
}
```

### Decision 3: "值类型" 的边界

z42 无清晰 value-type 语义，但：
- 基本类型 int/bool/float/double/char → 值类型
- string → **引用类型**（TypeRegistry.IsReference=true）
- class → 引用类型
- struct (isStruct=true 的 ClassDecl) → 值类型

**决定**：复用 `Z42Type.IsReferenceType` 已有路径。
- `class` flag 校验：`IsReferenceType(typeArg) == true`
- `struct` flag 校验：`IsReferenceType(typeArg) == false`（或 `typeArg is Z42ClassType ct && ct.IsStruct` — 但当前 Z42ClassType 没 IsStruct 字段，需看 SymbolTable 里的 struct 集合）

检查：struct 集合是否在 SymbolTable 可查？若无需新增 set，查 `ct.IsStruct` 需要增加字段。简化为 `!IsReferenceType`：
- int/bool → ✅（IsReferenceType=false）
- string → ❌（IsReferenceType=true，所以 struct 约束拒绝 string，正确）
- class Foo → ❌（拒绝 class 类型）
- struct Foo → `IsReferenceType` 对 Z42ClassType 返回 true（无视 isStruct）→ **错误**

需扩展：Z42ClassType 加 IsStruct 字段 或 SymbolTable 保留 struct 名集。最小侵入：`SymbolTable.IsStruct(className)` 借 Classes dict 查 Z42ClassType（但当前 Z42ClassType 无 isStruct 字段）。

**最终方案**：Z42ClassType 增加 `bool IsStruct` 字段（默认 false），SymbolCollector 设置。迁移全仓调用点。

### Decision 4: 互斥与组合

- `class` + `struct` → 报错
- `class` + 基类 → 基类蕴含 class，允许但冗余（不报错，不警告）
- `class` + 接口 → 允许
- `struct` + 接口 → 允许（即使 interface 约束无法满足 struct 通常，只是类型检查逻辑允许）
- `class` 或 `struct` 与泛型体内 T 的使用无关（只影响调用点）

### Decision 5: 错误消息

- 违反 class： `type argument 'int' for 'T' does not satisfy constraint 'class' on 'F'`
- 违反 struct：`type argument 'MyClass' for 'T' does not satisfy constraint 'struct' on 'F'`
- 互斥：`generic parameter 'T' cannot be both 'class' and 'struct'`（在 ResolveWhereConstraints 发）

## Implementation Notes

### Z42ClassType.IsStruct 迁移
- grep `new Z42ClassType(` → 全量加 `IsStruct: false` 或让 SymbolCollector.Classes.cs 读取 `cls.IsStruct` 传入
- 影响面：SymbolCollector.Classes.cs 构造一处；全仓应无其他构造点

### GenericConstraintBundle 扩展

```csharp
public sealed record GenericConstraintBundle(
    Z42ClassType? BaseClass,
    IReadOnlyList<Z42InterfaceType> Interfaces,
    bool RequiresClass = false,
    bool RequiresStruct = false)
{
    public static readonly GenericConstraintBundle Empty = new(null, []);
    public bool IsEmpty => BaseClass is null && Interfaces.Count == 0
                          && !RequiresClass && !RequiresStruct;
}
```

### ValidateGenericConstraints 扩展

```csharp
if (bundle.RequiresClass && !IsClassArg(typeArg))
    _diags.Error(..., "does not satisfy constraint 'class'");
if (bundle.RequiresStruct && !IsStructArg(typeArg))
    _diags.Error(..., "does not satisfy constraint 'struct'");

// helpers
bool IsClassArg(Z42Type t) => Z42Type.IsReferenceType(t);
bool IsStructArg(Z42Type t) => t switch
{
    Z42ClassType ct    => ct.IsStruct,
    Z42PrimType        => !Z42Type.IsReferenceType(t),  // int/bool/float/double/char
    Z42ErrorType or Z42UnknownType => true,
    _ => false,
};
```

## Testing Strategy

### 单元测试（TC16-TC21, 6 个）
```
Generic_ClassConstraint_Reference_Ok          — F<MyClass> 通过
Generic_ClassConstraint_Primitive_Error       — F<int> where T: class 违反
Generic_StructConstraint_Primitive_Ok         — F<int> where T: struct 通过
Generic_StructConstraint_RefType_Error        — F<MyClass> where T: struct 违反
Generic_ClassAndStruct_Exclusive_Error        — where T: class + struct 报错
Generic_ClassAndInterface_Combo_Ok            — where T: class + IDisplay
```

### Error goldens
- `errors/30_generic_class_violation/` — `F<int>` where T: class
- `errors/31_generic_struct_violation/` — `F<MyClass>` where T: struct
- `errors/32_generic_class_struct_exclusive/`（可选）

### 验证门
- `dotnet build` / `cargo build` / `dotnet test` 全绿
- `./scripts/test-vm.sh` 无变化（无新 run golden，flag 校验纯编译期）

## Risks

| 风险 | 缓解 |
|------|------|
| Z42ClassType 增加 IsStruct 字段破坏现有构造 | 默认值 false；grep 一处构造点统一迁移；L3-G2 / G2.5 测试全绿防回归 |
| `where T: class` 与调用点 `F<T>()` 无实参推断 | 调用点的 typeArg 推断沿用现有路径；flag 校验在推断之后发生 |
| Parser 的 `class` / `struct` 与顶层声明 keyword 冲突 | ParseWhereClause 只在 `where` 之后 / `:` 之后 / `+` 之后消费；顶层 decl 路径不受影响 |
