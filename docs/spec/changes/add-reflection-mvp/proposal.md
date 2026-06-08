# Proposal: 反射 MVP（GetType 路线全贯通）

> 状态：📋 DRAFT（待 User 确认）｜创建：2026-06-08｜类型：vm + stdlib（lang/vm 类，走完整流程）
> 占用子系统：`runtime` + `stdlib`（[ACTIVE.md](../ACTIVE.md) 登记）

## Why

0.3.x C 主线第一步（C0+C1）：让 z42 程序能在运行时**只读地**检视类型——字段、方法、基类、泛型实参。这是反射 MVP，也是自举编译器（B 主线 Semantics/TypeChecker）读类型元数据的直接客户（两线互为 dogfood）。

现状已比"从零"靠前，但有断点：
- `obj.GetType()` ✅ 已返回 `Std.Type`，但 `id: TypeId::UNRESOLVED` —— **Type 对象不带回真实 `TypeDesc` 的句柄**，无法枚举成员（[object.rs:43](../../../../src/runtime/src/corelib/object.rs#L43) 注释自指未来的 `expand-type-metadata`）。
- `Std.Type` ✅ 存在于 z42.core，但只有 `__name`/`__fullName`（[Type.z42](../../../../src/libraries/z42.core/src/Type.z42)）。
- 运行时 `TypeDesc` 已含：fields（名+type_tag）、vtable（方法名）、base_name、type_args、type_params；**方法签名（params/return）未加载进运行时**（`own_methods` 仅 `Box<[Box<str>]>` 名字）。

不做的后果：反射停在"拿到一个只有名字的 Type"，无法支撑自举编译器的元数据查询，也无法给用户提供 .NET 风格的 `GetFields/GetMethods`。

## What Changes

- `obj.GetType()` 返回的 `Std.Type` **携带真实 `TypeDesc` 句柄**（`NativeData::TypeHandle(Arc<TypeDesc>)`），使其方法能枚举成员。
- `Std.Type` 扩展：`Name` / `FullName` 属性、`BaseType`、`GetFields()`、`GetMethods()`、`GetMembers()`、`GetGenericArguments()`、`IsAbstract`/`IsSealed`/`IsStatic`。
- 新增 `Std.Reflection` 命名空间（**置于 z42.core**，避免与 Type 的循环依赖）：`MemberInfo` / `FieldInfo` / `MethodInfo` / `ParameterInfo`。
- 运行时 corelib 新增反射 builtins，从 `TypeDesc` 读字段/方法/基类/泛型实参。
- **Phase B（验证门控）**：把 zpkg TSIG 的方法签名（params/return/flags）加载进运行时 `TypeDesc`，使 `MethodInfo.GetParameters()` / `ReturnType` / `ParameterInfo` 完整可用。

## 范围划分（两阶段，Phase B 门控）

- **Phase A（零格式风险，保证交付）**：句柄 + 字段反射（FieldInfo 完整：名+类型）+ BaseType + GetGenericArguments + Type flags + GetMethods（**仅方法名**的 MethodInfo）。全部来自**已加载的运行时元数据**，无 zbc/zpkg 格式改动。
- **Phase B（契约门控）**：加载 TSIG 方法签名 → MethodInfo 完整（GetParameters/ReturnType/flags）+ ParameterInfo 实装。**前置验证**：确认 zpkg TSIG 方法签名在 reader 端可达且**无需格式 bump**（bytes 已持久化）。若发现需格式 bump 或工作量 >1.5× → **停下报告**（workflow 中断条件 7）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/types.rs` | MODIFY | `NativeData::TypeHandle(Arc<TypeDesc>)` 变体；（Phase B）`TypeDescCold` 加 `methods: Box<[MethodMeta]>` + accessor |
| `src/runtime/src/metadata/loader.rs` | MODIFY | （Phase B）把 TSIG 方法签名解析进 `MethodMeta` |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | （Phase B）若 TSIG 方法签名解析点在此则在此读 |
| `src/runtime/src/corelib/object.rs` | MODIFY | `builtin_obj_get_type` 改为存真实 `TypeDesc` 句柄到 `NativeData::TypeHandle` |
| `src/runtime/src/corelib/reflection.rs` | NEW | 反射 builtins：`__type_base` / `__type_fields` / `__type_methods` / `__type_generic_args` / `__type_flags`（+ Phase B `__method_params` / `__method_return`）|
| `src/runtime/src/corelib/reflection_tests.rs` | NEW | Rust 单元测试 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 reflection builtins + `mod reflection;` |
| `src/runtime/src/metadata/well_known_names.rs` | MODIFY | 加 `Std.Reflection.*` 类型名常量（如需）|
| `src/libraries/z42.core/src/Type.z42` | MODIFY | 扩展 Type：Name/FullName/BaseType/GetFields/GetMethods/GetMembers/GetGenericArguments/Is* |
| `src/libraries/z42.core/src/Reflection/MemberInfo.z42` | NEW | `Std.Reflection.MemberInfo` 基类 |
| `src/libraries/z42.core/src/Reflection/FieldInfo.z42` | NEW | `Std.Reflection.FieldInfo`（Name / FieldType / IsStatic）|
| `src/libraries/z42.core/src/Reflection/MethodInfo.z42` | NEW | `Std.Reflection.MethodInfo`（Name / ReturnType / GetParameters / Is*）|
| `src/libraries/z42.core/src/Reflection/ParameterInfo.z42` | NEW | `Std.Reflection.ParameterInfo`（Name / ParameterType / Position）|
| `src/libraries/z42.core/z42.core.z42.toml` | MODIFY | 若 source 非 glob 则登记新文件 |
| `src/libraries/z42.core/tests/reflection/source.z42` | NEW | z42.core dir-mode `[Test]` 反射用例 |
| `src/tests/types/reflection_basics.z42` | NEW | VM 端到端 golden（GetType→字段/方法/基类）|
| `docs/design/language/reflection.md` | NEW | C0 长期设计文档（API + 元数据映射 + 生命周期 + Deferred）|
| `docs/design/runtime/vm-architecture.md` | MODIFY | 反射元数据暴露机制（实现原理：TypeHandle / builtin 枚举路径）|
| `docs/roadmap.md` | MODIFY | 0.3.x C 主线 C1 进度标记 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记/释放 `runtime` + `stdlib` 锁 |

**只读引用**：
- `src/runtime/src/metadata/types.rs`（TypeDesc/FieldSlot/TypeDescCold 结构）— 理解元数据
- `src/compiler/z42.IR/ExportedTypes.cs`（ExportedMethodDef 签名字段）— Phase B 映射参考
- `src/libraries/z42.uri/`（包结构模板）

## Out of Scope

- `typeof(T)` 改为返回 `Std.Type`（当前返回字符串）—— 编译器改动，与正在进行的 `port-z42c-*` 自举移植需协调，单独 change 后做
- Attribute reflection（C3）—— 前置依赖"用户自定义 attribute 机制"spec
- `Method.Invoke` / `Activator.CreateInstance` / `Type.MakeGenericType` —— 强依赖 generic instantiation，0.5.x L3-R
- `GetProperties()` 完整版 —— 无 PropertyDesc 元数据；MVP 返回空或从 `get_/set_` 推导（design 裁决）
- 基础类型/数组的成员反射（`int`/`T[]` 的 GetType 返回 synthetic Type，无句柄 → 成员为空，文档说明）

## Open Questions

- [ ] `GetFields()` 默认返回全部实例字段（含继承，`TypeDesc.fields`）还是仅声明（`cold.own_fields`）？（建议：GetFields=全部；另加 `GetDeclaredFields` 留后续）
- [ ] `GetProperties()` MVP 行为：返回空 + 文档，还是从 `get_X`/`set_X` 方法名推导？（建议：返回空 + Deferred 条目）
- [ ] Phase B TSIG 方法签名 reader 可达性（实施首步验证）
