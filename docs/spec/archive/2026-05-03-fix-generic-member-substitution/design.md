# Design: generic 实例 member 链式访问 substitution

> **🟢 INVESTIGATED — 无需实施。** 见 proposal.md 探索结论。本文件
> 保留原设计草稿作为"设想 vs 实际"的历史对照。

## 实际探索结论

预期的修复点 **`BindMemberExpr` 链式 InstantiatedType receiver 处理**
经测试发现**已经工作**。链路追踪：

1. `slots[i]` 解析：`Z42ArrayType.Element` 是 `Z42InstantiatedType` （只要 SymbolTable.ResolveType 走 GenericType 分支正确返回 `Z42InstantiatedType` —— 实际确实如此，line 318-326）
2. `ElemTypeOf` 返回 `at.Element` —— 保持 InstantiatedType 完整性
3. `slots[i]` 类型节点是 InstantiatedType
4. `.sub` 进 BindMemberExpr，`target.Type is Z42InstantiatedType inst` 命中
5. BuildSubstitutionMap + SubstituteTypeParams 正确返回字段类型
6. `.sub.IsAlive()` 进 BindCallExpr / BindMemberExpr 链 —— `sub` 字段类型若是
   InterfaceType，进 line 99-114 的 InterfaceType 分支处理

D2b 当时报"_SubSlot has no member sub" 的真相是 Bug 1 (parse) 让类型节点
**降级为 Z42ClassType**（无 TypeArgs），所以走 line 96 的 ClassType 分支
报"has no member"。Spec 1 修复后类型解析正确，链路全通。

## 原始 Decisions（未实施）

### Decision 1: 修复点位置 — 选 A（每级独立处理）
未实施 —— BindMemberExpr 现有逻辑已每级独立处理。

### Decision 2: BuildSubstitutionMap 边界完善
未触发 —— 测试覆盖嵌套 / interface 字段 substitution 全通过，BuildSubstitutionMap 工作正确。

### Decision 3: 不动 ElemTypeOf
保留判断 —— ElemTypeOf 只返回 `at.Element` 是正确接口；不耦合 substitution。

### Decision 4（探索后新增）: SubstituteTypeParams Z42InterfaceType 分支不补
[TypeChecker.Generics.cs:374-391](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs#L374-L391) 的 `SubstituteTypeParams` 当前对 Z42InterfaceType 走 `_ => t` fall-through。理论上 InterfaceType.TypeArgs 中含 `Z42GenericParamType` 时应递归 substitute，但**当前测试用例都通过**说明实际路径用的是 SymbolTable.ResolveType 时已 substituted（在 generic class definition 加载时 type-args 已实在化），不依赖 SubstituteTypeParams 二次处理。

按"实现方案原则"，**实际触发 bug 不存在不应做防御性补丁**。如未来发现 InterfaceType.TypeArgs 留 raw type-param 漏 substitute 的真实场景再加。

## Implementation Notes

无 production code 改动。仅添加测试。

## Testing Strategy

- 7 个 unit test in `GenericMemberAccessTests.cs`：覆盖直接 / 数组中介 / 嵌套
  / interface 字段 / D2b 精确模式 / 两个 regression baseline
- dotnet test 全绿（基线 +7）
- 无 production 改动，stdlib / VM 不受影响
