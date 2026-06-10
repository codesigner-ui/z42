# Proposal: 对齐 Std.Type : Std.Reflection.MemberInfo 层级

## Why

统一反射模型下，`Std.Type` 当前**不是** `Std.Reflection.MemberInfo` 子类——而 .NET 中 `Type : MemberInfo`（类型本身可作为成员，如嵌套类型）。对齐后：
- `Type` 与 `FieldInfo`/`MethodInfo`/`PropertyInfo` 共享 `MemberInfo` 基类 → `Name` 由基类统一提供，而非现两套机制（`Type` 的 `[Native]` getter vs `MemberInfo` 的字段）。
- 为未来嵌套类型反射（`GetNestedTypes()` / `GetMembers()` 含嵌套类型）铺好层级地基。
- 消除 `Type.Name`（native getter 读 `__name`）与 `MemberInfo.Name`（字段）的机制分叉。

不做的代价：层级不对齐，`obj is MemberInfo` 对 Type 实例为假；嵌套类型反射无自然落点；两套 Name 机制长期并存。

来源：2026-06-09 "TypeInfo or unify" 设计讨论（User 裁决不拆 TypeInfo，维持统一 `Std.Type`）；Deferred `reflection-future-type-memberinfo-hierarchy`。

## What Changes

- `Std.Type` 改为 `public sealed class Type : MemberInfo`（短名基类，经全局短名 base 解析，**无需编译器改动**——已验证 `SymbolCollector.Classes.cs:370` 是 `_classes` 短名查找）。
- 移除 `Type` 的 `[Native("__type_name")] extern string Name { get; }`——改用继承自 `MemberInfo` 的 `public string Name;` 字段（否则同名 getter + 继承字段冲突）。
- 运行期 `build_type()` 把简单名写入继承的 `Name` 槽（此前只写 `__name`）；保留 `__name`/`__fullName` 字段（低层 golden / z42.test 直接读 `__name`，不破坏）。
- `__type_name` builtin 变为未使用 → 移除（reflection.rs + corelib/mod.rs 注销）。`FullName` 保留 native getter（`MemberInfo` 无 `FullName`）。

**无 zbc/zpkg 格式 bump**：`TypeDesc` 已支持 `base_name`(FQN) + 继承字段（cross-zpkg fixup）；`Std.Type` 类元数据住在 z42.core.zpkg → 纯 stdlib 重编 + runtime build_type 调整。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `: MemberInfo` 基类；移除 `[Native] Name { get; }`；调整注释 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `build_type()` 写继承 `Name` 槽；移除 `builtin_type_name` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注销 `__type_name` builtin |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | 加 `typeof(X) is MemberInfo` + Name-via-base 断言 |
| `src/tests/types/type_is_memberinfo.z42` | NEW | golden e2e：Type 实例的 MemberInfo 身份 + Name |
| `docs/design/language/reflection.md` | MODIFY | API 表 `Type : MemberInfo`；Deferred 条目标记落地；类层级说明 |

**只读引用**：
- `src/libraries/z42.core/src/Reflection/MemberInfo.z42`、`FieldInfo.z42`（继承先例）
- `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs:340-372`（base 解析，确认短名）
- `src/runtime/src/metadata/types.rs`（TypeDesc base_name/fields，确认无格式改动）

## Out of Scope

- `GetNestedTypes()` / 嵌套类型纳入 `GetMembers()`（本 change 只对齐层级，不加嵌套类型枚举）——留 `reflection-future-nested-types`。
- 移除 `__name`/`__fullName` 字段（保留作低层读取兼容；统一到 `Name`/`FullName` 属性留后续 cleanup）。
- 把 `Type` 迁出 `Std` prelude 到 `Std.Reflection`（破坏 `typeof`/`GetType` 免 import 人体工学——明确不动）。

## Open Questions

- [ ] `sealed class Type : MemberInfo` —— sealed 类带基类是否被 parser/typecheck 接受？（design.md Decision 2 待实施期插桩确认）
- [ ] `MemberInfo` 是否在 Type.z42 编译上下文的 `_classes` 中（同包 z42.core，应在）——实施期确认 base 链真实建立（`BaseType` 反射仍返 `Std.Object` 还是 `MemberInfo`？语义需明确）。
