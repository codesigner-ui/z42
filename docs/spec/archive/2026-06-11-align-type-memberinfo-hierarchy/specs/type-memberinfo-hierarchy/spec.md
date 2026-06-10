# Spec: Std.Type : Std.Reflection.MemberInfo

## ADDED Requirements

### Requirement: Type 是 MemberInfo 子类

#### Scenario: Type 实例满足 is MemberInfo
- **WHEN** `typeof(Circle) is MemberInfo`（或 `obj.GetType() is MemberInfo`）
- **THEN** 为 `true`（此前为 `false`）

#### Scenario: 经基类引用读取 Name
- **WHEN** `MemberInfo m = typeof(Circle); m.Name`
- **THEN** 返回 `"Circle"`（继承自 MemberInfo 的 `Name` 字段，由 VM 在构造 Type 实例时填充）

### Requirement: Name 经继承字段统一

#### Scenario: Type.Name 仍可用
- **WHEN** `typeof(Circle).Name`
- **THEN** 返回 `"Circle"`——解析到**继承的 `Name` 字段**（不再经 `[Native] __type_name` getter）

#### Scenario: 低层 __name 读取不破坏
- **WHEN** golden / z42.test 直接读 `t.__name`
- **THEN** 仍返回简单名（`__name` 字段保留，VM 继续填充）

#### Scenario: FullName 不变
- **WHEN** `typeof(Circle).FullName`
- **THEN** 返回 FQN（`FullName` 保留 native getter，MemberInfo 无此成员）

## MODIFIED Requirements

### Requirement: Std.Type 类层级

**Before:** `public sealed class Type`（无显式基类 → 隐式 `Std.Object`）；`Name` 经 `[Native("__type_name")]` getter。
**After:** `public sealed class Type : MemberInfo`；`Name` 继承自 `Std.Reflection.MemberInfo` 字段；`__type_name` builtin 移除。

## IR Mapping

无新 IR / 无格式 bump。`Std.Type` 的 `TypeDesc.base_name` 由 z42.core 重编后变为 `Std.Reflection.MemberInfo`；继承字段经现有 cross-zpkg fixup 合并。

## Pipeline Steps

- [x] Lexer / Parser — 无变化（短名基类走现有路径）
- [ ] TypeChecker — 确认 `sealed class : Base` + 跨命名空间短名 base 解析（应已支持，插桩确认）
- [ ] stdlib — Type.z42 基类 + 移除 Name getter
- [ ] VM (reflection.rs) — build_type 写 Name 槽 + 移除 builtin_type_name
