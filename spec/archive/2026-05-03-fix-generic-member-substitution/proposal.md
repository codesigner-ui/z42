# Proposal: 修复 generic 实例 member 访问 substitution propagation

> **🟢 INVESTIGATED — 无需实施。** 经探索发现"Bug 2"实为 Bug 1 (parse) +
> Bug 3 (equality) 的下游症状；Spec 1 + Spec 2 落地后此 spec 的全部用例
> 自动通过。本 spec 不做实施改动，但**保留 7 个回归测试**作为防御性
> 覆盖（`GenericMemberAccessTests.cs`），归档以记录探索过程。

## 原始 Why

[TypeChecker.Exprs.Members.cs:17-60](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs#L17-L60) `BindMemberExpr()` 处理 `Z42InstantiatedType` 的成员访问时，对**直接成员**走 `BuildSubstitutionMap` + `SubstituteTypeParams` 替换正确。

D2b 实施时记录的失败：

```z42
class _SubSlot<T> { public ISubscription<T> sub; }
_SubSlot<TD>[] slots;
slots[i].sub.IsAlive()    // → "_SubSlot has no member sub"
```

历史归因为 substitution propagation 丢失。

## 探索结论（2026-05-03）

实施前用 7 个 unit test（含 D2b 原报错的精确模式 `Slot<Action<T>>[].sub.IsAlive()`）探测：

- ✅ Array_InstantiatedElement_FieldAccess
- ✅ Array_InstantiatedElement_ChainedFieldMethod
- ✅ Array_NestedInstantiatedElement_FieldAccess
- ✅ Direct_Instantiated_FieldAccess_Regression
- ✅ Direct_Instantiated_MethodCall_Regression
- ✅ Interface_Field_TypeArg_Substitution
- ✅ D2b_ExactPattern_SubSlot_With_ActionT

**全部通过，无实施需要。**

复盘原 D2b 报 "_SubSlot has no member sub" 的真实根因：

1. **Bug 1 (parse) 阻塞**：`_SubSlot<Action<T>>[]` 的 `>>` 解析失败 → TypeChecker
   收到 degraded 类型 → 走 Z42ClassType 分支报"has no member"。Spec 1 修复后
   parse 正确，TypeChecker 走 Z42InstantiatedType 分支。
2. **Bug 3 (equality) 阻塞**：interface 字段赋值失败遮盖了下游成员访问。
   Spec 2 修复后赋值通过。
3. **真正的 substitution 机制**（`SubstituteTypeParams` + `BuildSubstitutionMap`
   + BindMemberExpr 现有 InstantiatedType 分支）一直工作正确 —— 只是上游
   parse / equality 故障让它显得有问题。

按 `.claude/rules/workflow.md` "实现方案原则"，**实际触发 bug 不存在不应做防御性补丁**。`SubstituteTypeParams` 当前对 Z42InterfaceType 的 fall-through `_ => t` 行为是正确的（接口字段在使用 InterfaceType.TypeArgs 时已 substituted；如未来发现真实失败再加）。

## What Changes

- 仅添加 `src/compiler/z42.Tests/GenericMemberAccessTests.cs` 7 个测试作为回归防御
- 不改任何 production code

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Tests/GenericMemberAccessTests.cs` | NEW | 7 个 generic member access 回归测试 |

## Out of Scope

- TypeChecker.Exprs.Members.cs / TypeChecker.Generics.cs production code 修改
- `SubstituteTypeParams` Z42InterfaceType 分支添加（当前未触发实际 bug）
- 协变 / 逆变（D-4）
- 跨 generic interface 的 impl 扩展方法（D-2）
