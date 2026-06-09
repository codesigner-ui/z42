# Design: 反射 MVP

## Architecture

```
用户代码  obj.GetType().GetFields()
            │
            ▼
z42.core   Std.Type / Std.Reflection.{FieldInfo,MethodInfo,ParameterInfo}
            │  (extern 方法 → builtin 调用)
            ▼
runtime    corelib/reflection.rs builtins
            │  __type_fields(typeObj) / __type_methods / __type_base / __type_generic_args / __type_flags
            ▼
           NativeData::TypeHandle(Arc<TypeDesc>)  ← Type 对象携带的真实句柄
            │
            ▼
metadata   TypeDesc { fields, vtable, base_name, cold{own_methods, type_args, methods(Phase B)} }
```

核心思路：`GetType()` 时手上就有对象的真实 `Arc<TypeDesc>`（`rc.type_desc()`），当前实现把它丢了并合成一个空壳。本设计把这个 Arc **存进 Type 对象的 `NativeData`**，反射 builtins 据此枚举。

## Decisions

### Decision 1: Type 如何携带真实 TypeDesc —— NativeData::TypeHandle
**问题：** `Std.Type` 是 `ScriptObject`，slots 是 `Value`，装不下 `Arc<TypeDesc>`。
**选项：**
- A — 在 Type 的 slot 存 `__typeId`(i64)，builtin 按 id 去 `type_registry_vec` 查。缺点：TypeId 按 Module 局部，跨 zpkg 需定位 module，复杂易错。
- B — 存 `__fullName`，builtin 按名去 `type_registry` 查。缺点：依赖全局/合并注册表，且名字解析有歧义成本。
- C — **新增 `NativeData::TypeHandle(Arc<TypeDesc>)`，Type 对象直接持有真实 Arc**。
**决定：** 选 C。GetType 时已持有 Arc，直接存最直接、O(1)、跨 zpkg 安全（Arc 就是真身）；与现有 `NativeData::WeakRef` 等变体同款模式。slots 仍保留 `__name`/`__fullName` 供廉价读取与向后兼容。

### Decision 2: 反射类型放 z42.core（Std + Std.Reflection）
**问题：** 独立 z42.reflection 包与 z42.core 循环依赖（`Object.GetType()` 返回 `Type` → Type 在 core；`Type.GetFields()` 返回 `FieldInfo` → 若 FieldInfo 在 reflection 包，core↔reflection 互依）。
**决定：** 仿 .NET mscorlib——`Std.Type` 与 `Std.Reflection.{MemberInfo,FieldInfo,MethodInfo,ParameterInfo}` 同在 **z42.core**，不同命名空间。独立 `z42.reflection` 包留给 0.5.x 的 `Method.Invoke`/`Activator`（那些可单向依赖 core）。（User 2026-06-08 裁决）

### Decision 3: 两阶段，Phase B 契约门控
**问题：** 运行时 `TypeDesc` 只有方法名（`own_methods: Box<[Box<str>]>` + vtable），**无方法签名**（params/return）。完整 `MethodInfo`/`ParameterInfo` 需把 zpkg TSIG 的 `ExportedMethodDef` 加载进运行时。
**选项：** A — 一次做全（含签名加载）；B — 拆两阶段。
**决定：** 选 B。
- **Phase A**（零格式风险）：句柄 + FieldInfo（字段元数据运行时已全有）+ BaseType + GetGenericArguments + Type flags + MethodInfo(仅 Name)。**保证交付、完全可验证**。
- **Phase B**（门控）：把 TSIG 方法签名加载进 `TypeDesc.cold.methods: Box<[MethodMeta]>`（`MethodMeta { name, params: [(name,type_tag)], return_type, is_static, is_virtual, is_abstract, visibility }`）→ MethodInfo 完整 + ParameterInfo 实装。
  **前置验证（实施首步）**：确认 zpkg TSIG 方法签名 bytes 在 Rust reader 端可达。bytes 已由 C# `ZpkgWriter.Tsig.cs` 持久化（`WriteMethodDef`），预期**无需格式 bump**——只是 reader 多解析一段。**若验证发现 reader 跳过该段且需改 wire 格式（zbc/zpkg minor bump），或工作量 >1.5× tasks 估计 → 停下报告 User**（workflow 中断条件 7）。

### Decision 4: GetFields 默认含继承；GetProperties MVP 返空
**决定：**
- `GetFields()` = 全部实例字段（含继承，读 `TypeDesc.fields`，base 在前）。声明级 `GetDeclaredFields()`（读 `cold.own_fields`）留后续。
- `GetProperties()` MVP 返回空数组 + 在 reflection.md Deferred 记一条（无 PropertyDesc 元数据；自动属性降解为 field+get_/set_）。从方法名推导属性留后续。

### Decision 5: BaseType 根类返回
**决定：** 有 `base_name` → 返回对应 Type；无显式基类 → 返回 `Std.Object` 的 Type（与"一切皆 Object"一致）；`Std.Object` 自身的 BaseType 返回 `null`（参照 C# `typeof(object).BaseType == null`）。

### Decision 6: 参照 C# 反射语义（User 2026-06-08）
基准 = C# `System.Reflection`。具体落到 MVP：
- **`FieldType` / `ReturnType` / `ParameterType` 返回 `Std.Type`（不是字符串）** —— 参照 C# `FieldInfo.FieldType : Type`。实现：从 `type_tag`（"int"/"Demo.Box"）构造 Std.Type——能在 `type_registry` 解析到 TypeDesc 则带句柄，基础类型（int/long/bool/...）走 synthetic Type（`Name` = 该 tag），与 GetType 对基础类型的退化一致。
- **`Name` = 非限定名，`FullName` = 命名空间限定名**（C# `Type.Name` / `Type.FullName`）。
- **`GetFields()` / `GetMethods()` 默认含继承的 public 成员**（C# 无 BindingFlags 重载语义）。字段读 `TypeDesc.fields`（已含继承，base 在前）；方法 vtable 已含继承槽。声明级 `GetDeclaredFields()`/`GetDeclaredMethods()` 留后续。
- **`GetProperties()` MVP 返空** —— 唯一与 C# 的已知偏差，因无 PropertyDesc 元数据（reflection.md Deferred 记明"待 property 元数据落地后对齐 C#"）。
- **MemberInfo 继承**：`FieldInfo`/`MethodInfo` : `MemberInfo`（C# 层级），`MemberInfo` 有 `Name` / `DeclaringType`。

## Implementation Notes

- **builtin 入参**：每个 `__type_*` builtin 首参是 Type 的 `Value::Object`，从其 `native` 取 `NativeData::TypeHandle(Arc<TypeDesc>)`；非 TypeHandle（synthetic/退化）→ 返回空数组 / null（lenient，不 bail）。
- **FieldInfo/MethodInfo 构造**：builtin 返回基础数据（如 `Value::Array` of 小对象，或并行的 name/type 数组），由 z42 端 `Type.GetFields()` 包装成 `FieldInfo[]`；或 builtin 直接 alloc `FieldInfo` ScriptObject。倾向后者（一致于 GetType 直接 alloc Type）：builtin 按 z42.core 里 `FieldInfo` 的 TypeDesc 模板 alloc，填 slots。需要 `FieldInfo`/`MethodInfo`/`ParameterInfo` 的 TypeDesc 在 runtime 可得（按全限定名从 `type_registry` 取，z42.core 加载后即在）。
- **NativeData::TypeHandle 的 derive**：`NativeData` 是堆对象运行时态、不序列化；新增 Arc 变体不影响持久化。注意 `Clone`（若 NativeData derive Clone，Arc clone 廉价）。
- **well_known_names**：加 `STD_REFLECTION_FIELDINFO` 等常量供 builtin alloc 时按名查 TypeDesc。
- **GetType 退化路径**：`Value::Array`/`Value::Str`/基础类型仍走旧 synthetic 分支（无 TypeHandle）；文档说明其成员查询为空。

## Testing Strategy

- **Rust 单元**（`corelib/reflection_tests.rs`）：构造带 fields/base/type_args 的 `TypeDesc`，调各 builtin，断言枚举结果；TypeHandle 缺失时返空不 panic。
- **z42 `[Test]`**（`z42.core/tests/reflection/source.z42`）：定义 Point/Derived:Base/Box<int>/abstract/sealed，断言 GetFields/GetMethods/BaseType/GetGenericArguments/Is* 行为。
- **VM golden**（`src/tests/types/reflection_basics.z42`）：端到端 GetType→字段/方法名/基类，stdout golden。
- **GREEN**：`z42 xtask.zpkg test`（含 dotnet / vm / cross-zpkg / lib 全 stage）；z42.core 改动触发 stdlib regen。
