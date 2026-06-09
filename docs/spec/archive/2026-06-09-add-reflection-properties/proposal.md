# Proposal: Type.GetProperties() — 反射属性视图（0.3.x C 主线收尾）

## Why

反射 MVP（add-reflection-mvp + C2/C3）已覆盖字段、方法、基类、泛型实参、自定义 attribute，但 `Type.GetProperties()` 仍返空（reflection-future-properties）。这是 C# `System.Type` 只读反射面里唯一**能在 0.3.x 干净补齐**的缺口——属性元数据可纯运行期从 `get_<X>`/`set_<X>` 方法约定派生，无需任何编译器改动、无需新 zbc 字段。

补齐后，反射在 0.3.x 的**可达表面**即完整；剩余项要么 0.5.x 泛型实例化硬墙（`Invoke`/`Activator`/`MakeGenericType`/`GetAttribute<T>`），要么需 zbc 格式 bump（type flags / static fields / field+param attr targets）会撞正在进行的 `port-z42c-zbc-writer` 自举移植，均明确排除在本变更外。

## What Changes

- 新增 stdlib `Std.Reflection.PropertyInfo : MemberInfo`（`PropertyType` / `CanRead` / `CanWrite`；**无** `GetValue`/`SetValue`——那依赖 0.5.x `Invoke`）。
- `Std.Type` 加 `extern PropertyInfo[] GetProperties()`（`[Native("__type_properties")]`）。
- 运行时新增 `__type_properties` builtin：扫 `TypeDesc` 的 vtable + own_methods（含继承），按 `get_<Name>`（0 参）/`set_<Name>`（1 参）配对派生 PropertyInfo，eager 填充实例。handle-less Type（基础类型/数组）返空。
- 文档 + golden + dogfood [Test]。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/Reflection/PropertyInfo.z42` | NEW | `PropertyInfo : MemberInfo`，VM 写 PropertyType/CanRead/CanWrite |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | 加 `[Native("__type_properties")] extern PropertyInfo[] GetProperties()` |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | 新 `builtin_type_properties` + `build_property_info` 辅助 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `("__type_properties", reflection::builtin_type_properties)` |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | 加 GetProperties dogfood [Test]（含只读 / 读写 / 继承属性 / 无属性类）|
| `src/tests/types/get_properties.z42` | NEW | golden：属性枚举端到端 |
| `docs/design/language/reflection.md` | MODIFY | API 表加 PropertyInfo + 实现原理段 + Deferred 移除 reflection-future-properties |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 同步（若有 reflection-future-properties 索引行）|

**只读引用**：

- `src/runtime/src/corelib/reflection.rs`（现有 `builtin_type_methods` / `build_method_info`）— 派生与填充的模板
- `src/libraries/z42.core/src/Reflection/MethodInfo.z42` / `MemberInfo.z42` — 基类 + 字段约定
- `src/runtime/src/metadata/types.rs`（`TypeDesc.vtable` / `cold.own_methods`）— 数据来源

## Out of Scope

- `GetValue` / `SetValue`（需 0.5.x `Invoke`）。
- Type flags（`IsAbstract`/`IsSealed`/`IsStatic`）、static fields 纳入 GetFields、field/parameter attribute targets——**均需 zbc 格式 bump**，撞 `port-z42c-zbc-writer`，post-port 再做。
- 把 properties 纳入 `GetMembers()`（改现有 members 输出）——独立 follow-up。
- 隐藏 auto-property backing field（让其不出现在 GetFields）——需区分 backing vs 真实字段的元数据，独立。

## Open Questions

- [ ] 无（设计已闭合，见 design.md）。
