# Design: L3-G2 泛型约束

## Architecture

```
 Source        AST                TypeCheck                           IrGen / VM
 ─────────────────────────────────────────────────────────────────────────────
 where T:      WhereClause       Z42GenericParamType                 (no change)
  I + J        ├─ GenericConstraint  Name, Constraints: List<Z42IType>   VCall
               │   TypeParam: "T"      ↓ pushed with PushTypeParams
               │   Constraints:[I,J]   ↓ member lookup走 constraint
                                        vtable
                                      ↓ call site 校验
                                        ImplementsAll(typeArg, Constraints)
```

## Decisions

### Decision 1: 语法选择 `+` vs `,`

**问题**：多约束组合用什么分隔？

**选项**：
- A. Rust `+`：`where T: I + J`（约束组合感强，`,` 用于跨参数）
- B. C# `,`：`where T: I, J`（跨参数需 `where T: I where K: J` 重复关键字）

**决定**：A（Rust 风格）。
- 与用户记忆 "泛型约束要向 Rust 靠近" 一致
- `+` 表达"T 同时具有多个能力"比 `,` 更直观
- 跨参数用 `,` 继承 C# 熟悉感：`where K: I, V: J`

### Decision 2: `Z42GenericParamType` 的约束存储

**问题**：约束信息放哪？

**选项**：
- A. `Z42GenericParamType` 新增 `Constraints: List<Z42InterfaceType>`
- B. 在 SymbolTable 维护独立的 type_param → constraints 映射
- C. 挂在 FunctionDecl/ClassDecl 上，每次查找重新解析

**决定**：A。
- 最就近：类型查询时约束直接可用，无需回查 AST
- 兼容 L3-G1 路径：`Z42GenericParamType` 已作为 T 的载体
- 约束唯一绑定在定义时，记录一份即可

### Decision 3: 调用点约束校验时机

**问题**：`Max<MyClass>(x, y)` 在何处校验约束？

**选项**：
- A. TypeChecker.BindGenericCall：推断/解析类型参数后立即校验
- B. IrGen 阶段校验（延后）
- C. 运行时校验（VM）

**决定**：A。编译期即知 type args 和约束，早失败。IrGen/VM 不做额外校验，保持代码共享简洁。

### Decision 4: 约束方法调用的 Call 形式

**问题**：`a.CompareTo(b)` 其中 a: T where T: IComparable<T>，生成 Call 还是 VCall？

**选项**：
- A. 统一 VCall：运行时按 a 的动态类型走 vtable
- B. "Generic Call"：新增指令，延迟到实例化时分发
- C. 单态化特化：每个 type arg 生成一份 Call

**决定**：A（VCall）。
- 代码共享策略下，T 的动态类型运行时才知，必然需要动态分发
- 现有 VCall 机制已成熟，zero 新 IR 指令
- 与 C# 共享代码策略一致
- 性能差异（VCall vs Call）属 L3 后期优化（trait 静态分发）范畴

### Decision 5: 约束元数据是否写入 zbc（L3-G2 范围内）

**问题**：zbc SIGS/TYPE section 是否追加约束信息？

**选项**：
- A. 不写入：仅编译期使用，TypeChecker 依赖 AST/SymbolTable
- B. 写入：支持未来反射、IDE 工具、外部二进制的 TypeChecker、VM 侧约束校验

**决定**：A（L3-G2 暂不写入），但**确认 L3-G3 阶段会补齐**（见下）。
- L3-G2 范围不含反射和 VM 侧校验
- 增加二进制格式版本号变更的成本；与 L3-G3 反射启动时一并加，避免两次格式变更
- 本阶段 TypeChecker 完整校验足以保证 trusted zbc 的安全

### Future Work（L3-G3+ 明确纳入范围）

> 用户要求记录：VM 后续要支持反射和约束校验，不能遗漏。

**L3-G3 阶段补齐的 VM 侧能力**：

1. **zbc 二进制扩展**：
   - SIGS/TYPE section 追加 constraint 元数据：`tp_count × (tp_name_idx, constraint_count, [constraint_type_idx])`
   - 格式需与 L3-G1 的 `type_params` 兼容演进（不破坏已有 zbc）
2. **VM 校验（untrusted zbc 场景）**：
   - `loader.rs` 加载阶段读取约束到 `TypeDesc.constraints` / `Function.constraints`
   - ObjNew / 泛型函数 Call 时校验 type_args 实现约束（以 TypeDesc.vtable interface 为参考）
   - 运行时校验失败 → 结构化 VM 异常（非 panic）
3. **反射接口**：
   - `type.TypeParams` / `type.Constraints` 暴露给 z42 代码
   - `t is IComparable<T>` 运行时判断依据约束元数据
4. **跨模块 TypeChecker**：
   - 外部 `.zpkg` 依赖的泛型签名携带约束，消费方编译期校验
   - 需 TSIG section 同步扩展（由 ExportedMethodDef / ExportedClassDef 承载 constraint 字段）

**为什么延迟到 L3-G3**：
- L3-G2 单次变更保持小范围、可验证；避免与反射特性混合
- 二进制格式变更集中在 L3-G3 一次性完成（major version bump）
- 约束元数据的完整设计与关联类型（L3-G3 主题）耦合 — 关联类型本身就是约束的一部分

## Implementation Notes

### Parser

`where` 关键字已注册 token。解析位置：
- 函数：`T Max<T>(T a, T b) where T: I { ... }`（在返回类型/参数列表后、`{` 或 `=>` 前）
- 类：`class Box<T> where T: I { ... }`（在 base/interface 列表后、`{` 前）
- 接口：同类

```csharp
// TopLevelParser.Helpers.cs
public static WhereClause? ParseWhereClause(ref TokenCursor cursor)
{
    if (!cursor.CurrentIs(TokenKind.Where)) return null;
    cursor.Advance();
    var constraints = new List<GenericConstraint>();
    do {
        var typeParam = ExpectIdentifier(ref cursor);
        ExpectToken(ref cursor, TokenKind.Colon);
        var types = new List<TypeExpr>();
        types.Add(TypeParser.ParseType(ref cursor));
        while (cursor.CurrentIs(TokenKind.Plus)) {
            cursor.Advance();
            types.Add(TypeParser.ParseType(ref cursor));
        }
        constraints.Add(new GenericConstraint(typeParam, types, span));
    } while (ConsumeIf(ref cursor, TokenKind.Comma));
    return new WhereClause(constraints, span);
}
```

**歧义消除**：表达式中的 `+` 从不在 `where` 子句位置（`where` 后必有 identifier）。`+` 作为约束分隔符只在 TypeExpr 上下文。

### TypeChecker

`Z42GenericParamType` 扩展：
```csharp
public sealed record Z42GenericParamType(
    string Name,
    IReadOnlyList<Z42InterfaceType> Constraints  // 空表示无约束
) : Z42Type;
```

**成员查找**（TypeChecker.Exprs 处理 `t.Member`）：
```csharp
if (receiver is Z42GenericParamType gp && gp.Constraints.Count > 0) {
    foreach (var iface in gp.Constraints) {
        if (iface.Methods.TryGetValue(memberName, out var funcType))
            return (funcType, dispatch: Virtual);
    }
    diag.Error(E0104 MemberNotFound, ...);
}
```

**调用点校验**（TypeChecker.Calls 处理泛型函数/构造器）：
```csharp
static bool ImplementsAll(Z42Type typeArg, IReadOnlyList<Z42InterfaceType> constraints, SymbolTable st) {
    foreach (var c in constraints)
        if (!TypeUtil.ImplementsInterface(typeArg, c, st))
            return false;  // 定位到具体失败接口用于 diag
    return true;
}
```

**已有 API 复用**：
- `_classInterfaces: Dictionary<ClassName, HashSet<InterfaceName>>` (SymbolCollector.Classes) → 判断 `MyClass : IComparable<MyClass>` 是否实现。
- `TypeUtil.IsSubclassOf` 处理类继承；扩展/复用 `ImplementsInterface`。

### IrGen

**无改动**。T 映射为 Ref；约束方法调用进入原有的 VCall 路径（通过 TypeChecker 生成的 BoundMethodCall dispatch 字段为 Virtual）。

### VM

**无改动**。VCall 已按 ScriptObject.TypeDesc.vtable 查找方法；type_args 未填充不影响。

## Testing Strategy

### 单元测试（TypeCheckerTests.cs）

```
TC1: Generic_SingleConstraint_MethodCallOk        -- 约束方法可调用
TC2: Generic_MultiConstraint_BothMethodsOk        -- + 组合
TC3: Generic_CrossParamConstraint_Ok              -- where K: I, V: J
TC4: Generic_CallSite_TypeArgImplements_Ok        -- Max<MyClass> 合规
TC5: Generic_CallSite_TypeArgMissing_Error        -- Max<Plain> 报 E0103
TC6: Generic_MethodOnUnconstrainedT_Error         -- T.Method() 无约束报 E0104
TC7: Generic_ClassField_ConstraintMethodCall_Ok   -- 类体内 this.field.M()
TC8: Generic_Inferred_ConstraintSatisfied_Ok      -- 推断 T=MyClass + 约束校验
```

### Round-trip 测试（ZbcRoundTripTests.cs）

- 约束不写入 zbc，round-trip 仍保持 TypeParams 不变（现有 G1 测试已覆盖，G2 新增空约束用例）

### Golden test `70_generic_constraints`

```z42
// source.z42
class Num : IComparable<Num> {
    int value;
    Num(int v) { this.value = v; }
    public int CompareTo(Num other) {
        if (this.value > other.value) return 1;
        if (this.value < other.value) return -1;
        return 0;
    }
}

T Max<T>(T a, T b) where T: IComparable<T> {
    return a.CompareTo(b) > 0 ? a : b;
}

void Main() {
    var a = new Num(3);
    var b = new Num(7);
    var m = Max(a, b);
    Console.WriteLine(m.value);  // expected: 7
}

// expected_output.txt
7
```

### 错误用例 golden test（errors/）

- `generic_constraint_not_satisfied/source.z42` + expected error code
- `generic_member_on_unconstrained_t/source.z42` + expected error code

### 验证门

- `dotnet build` + `cargo build` 无错误
- `dotnet test` 100% 通过（新增 ≥ 8 TC + 2 round-trip + 1 golden）
- `./scripts/test-vm.sh` 100% 通过（interp + jit 各 67 个 = 134）

## Risks

| 风险 | 缓解 |
|------|------|
| `where` 解析与后续 `{` 之间的 token 干扰 | 严格 `do...while(,)`，不消费 `{` |
| 约束接口泛型实例化（`IComparable<T>`）中 T 的解析 | 复用 L3-G1 的 Z42GenericParamType 解析，`Constraints` 存带 T 的 interface type |
| class 约束继承（泛型类 B<T> extends A<T>） | 出 Scope，留到 L3-G3/G4 |
