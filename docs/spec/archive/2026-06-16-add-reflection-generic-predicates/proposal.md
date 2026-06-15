# Proposal: Type 泛型/基元类别谓词（IsGenericType / IsGenericTypeDefinition / IsPrimitive）

## Why

反射已有 `GetGenericArguments()` 但缺**配套的泛型类别谓词**——无法问"这是不是泛型类型 / 开放定义"。
`IsPrimitive` 也缺失。三者都能**纯运行期派生**自已加载的元数据（`TypeDescCold.type_params`/`type_args`
+ 类型名），无需任何格式 bump，是完成泛型反射叙事的低风险增量。

- `Type.IsGenericType`：有类型参数（`Box<T>`）→ true。
- `Type.IsPrimitive`：类型名是基元（`int`/`long`/`short`/`byte`/`sbyte`/`uint`/`ulong`/`ushort`/`float`/`double`/`bool`/`char`，含 `Std.IntXX` 等 BCL 名）→ true。
- 两个 native builtin（`__type_is_generic` / `__type_is_primitive`）+ Type.z42 两个 extern 属性。
- **无格式 bump**（纯运行期派生）。

> **实施期收窄（2026-06-14）**：原计划含 `IsGenericTypeDefinition`，实施中发现 z42 `typeof(Box<int>)` 经 `Z42TypeName(Z42InstantiatedType)` 只 emit 定义名（丢实例化实参），运行期解析到与开放定义**同一个** TypeDesc（`type_args` 恒空）——开放定义与实例化不可区分，`IsGenericTypeDefinition` 无法可靠实现。根因在编译器（typeof 不携带 type args），属独立增量。本增量收窄为 `IsGenericType` + `IsPrimitive`，`IsGenericTypeDefinition` 入 Deferred。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_is_generic` / `builtin_type_is_primitive` + 基元名集合助手 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册两个 builtin |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsGenericType` / `IsPrimitive` extern 属性 |
| `docs/design/language/reflection.md` | MODIFY | 用法 + 实现原理；Deferred 加落地标记 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 占用 runtime/stdlib；归档释放 |
| `src/tests/types/generic_predicates.z42` | NEW | golden e2e（泛型实例化/开放定义/非泛型/基元/类） |

**只读引用**：

- `src/runtime/src/corelib/well_known_names.rs` — 基元 BCL 名常量
- `src/runtime/src/corelib/reflection.rs`（`builtin_type_is_value_type` / `builtin_type_generic_args`）— 同款 builtin 模式

## Out of Scope

- `IsClass` / `IsInterface` / `IsEnum`：需类别元数据（接口/枚举当前不产 TYPE 条目），属独立格式-bump 增量，不在此。
- `IsGenericTypeDefinition`：收窄延后（typeof 不携带实例化 type args，运行期开放定义与实例化不可区分）——前置 = 编译器让 typeof 携带 type args。
- `GetGenericTypeDefinition()`（返回开放定义 Type 句柄）：需开放类型 handle 处理，延后。
- `MakeGenericType` / 泛型 instantiation：0.5.x（见 reflection-future-method-invoke）。

## Open Questions

- 无（行为完全由已加载元数据派生，无歧义）。
