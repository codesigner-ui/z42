# Proposal: Z42Type record 结构相等修复

## Why

C# record 默认 `Equals` 对 `IReadOnlyList<T>` 字段做**引用比较**而非 element-wise。
导致以下 record 的两个结构相同实例报"不相等"：

| Record | 受影响字段 | 后果 |
|--------|-----------|------|
| `Z42InstantiatedType` | `TypeArgs` | `Foo<int>` ≠ `Foo<int>`（不同对象）—— [IsAssignableTo:82-86](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs#L82-L86) 已 element-wise workaround，但 `==` / HashSet / Dict 仍坏 |
| `Z42InterfaceType` | `TypeArgs` + `Methods` + `StaticMembers` + `TypeParams` | **完全无 workaround**：D2b `this.advanced[i] = wrapper` 报 "cannot assign ISubscription<(T)->void> to ISubscription<(T)->void>" |
| `Z42FuncType` | `Params` | [IsAssignableTo:63-74](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs#L63-L74) 已 element-wise workaround，但 `==` / HashSet 仍坏 |

[TypeChecker.Generics.cs:232-249](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs#L232-L249) 的 `TypeArgEquals` 是另一个独立的 element-wise helper（在 IsAssignableTo 路径外用）。

这是 D2b ISubscription wrapper 的硬阻塞 —— interface 字段赋值无法通过类型检查。

按 `.claude/rules/workflow.md` "修复必须从根因出发"，**正确做法是修每个 record 的 `Equals` / `GetHashCode`**，不在 IsAssignableTo 上加更多症状级补丁分支。

## What Changes

- 三个 record (`Z42InstantiatedType` / `Z42InterfaceType` / `Z42FuncType`) 显式 override
  `Equals(同类型?)` + `GetHashCode()`（C# record 自动从 strongly-typed 派生 `Equals(object?)`）
- 实现：标量字段保留 record 默认；列表字段元素级递归 `Z42Type.Equals`
- IsAssignableTo 现有 workaround 分支**保留**（防御性 + 行为不变），但触底 `target == source`（line 45）现在对这三个 record 也能正确返回 true
- 不动 `Z42GenericParamType.InterfaceConstraints` —— 当前未触发任何 bug，按 minimum scope；如未来发现再加

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | 三个 record 加 Equals/GetHashCode override |
| `src/compiler/z42.Tests/Z42TypeEqualityTests.cs` | NEW | 结构 equality 单元测试（覆盖三个 record + HashCode 一致性） |

**只读引用**：

- `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs:473-489` — RequireAssignable / IsAssignableTo 调用入口
- `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs:232-249` — `TypeArgEquals` 现有 helper
- `src/libraries/z42.core/src/MulticastAction.z42` — D2b 真实场景

## Out of Scope

- 类型 intern table（备选方案，不采用）
- `Z42GenericParamType` / `Z42ClassType` 的 list 字段 equality（暂未触发 bug，按 minimum scope）
- `TypeArgEquals` helper 重构（评估为 dead-code 后单独 cleanup spec 处理）
- 协变 / 逆变（D-4 deferred）
- IsAssignableTo workaround 分支删除（保留作为防御性，不动）

## Open Questions

- [ ] `Z42InterfaceType.Methods`（IReadOnlyDictionary）字段：当前实践中两次构造同一接口实例化时 Methods 字典对象往往是同一引用（来自 ClassType / InterfaceCollector 缓存），所以默认引用比较通常正确。本 spec 不做字典内容深比，留观察；触发 bug 再加 spec
