# Design: Type 泛型/基元谓词

## Architecture

```
已加载元数据（无新 wire）：
  TypeDescCold.type_params / type_args  ← 既有（GetGenericArguments 已用）
  TypeDesc.name                          ← 既有
        │
        ▼
__type_is_generic       = !type_params().is_empty() || !type_args().is_empty()
__type_is_generic_def   = !type_params().is_empty() && type_args().is_empty()
__type_is_primitive     = is_primitive_name(td.name)
        │
        ▼
Std.Type.{IsGenericType, IsGenericTypeDefinition, IsPrimitive}
```

## Decisions

### Decision 1: 纯运行期派生，零格式 bump

**问题：** 三谓词的数据从哪来。
**决定：** 全部派生自已加载的 `type_params`/`type_args`（泛型）+ 类型名（基元）。`GetGenericArguments()` 已证明这些数据在运行期可用。**无 wire 变更、无 zbc/zpkg bump、无 C# 编译器改动**——这是本增量选型的核心（低风险、快）。

### Decision 2: IsGenericTypeDefinition 收窄延后（实施期架构发现）

**问题：** 原设计含 `IsGenericTypeDefinition`（区分开放定义 vs 实例化）。
**实施期发现：** z42 `typeof(Box<int>)` 经 `Z42TypeName(Z42InstantiatedType)` 只 emit **定义名**（`Demo.Box`，丢实例化实参），运行期 `make_type_from_name` 解析到与开放定义**同一个** TypeDesc（`type_args` 恒空——loader 把 `type_args: vec![]`）。故 `typeof(Box<int>)` 与开放定义运行期**不可区分**，`IsGenericTypeDefinition`（判据 type_args 空）会把实例化误判为定义。既有 `GetGenericArguments` 测试正因此是 "best-effort"（`if (args.Length == 1)`）。
**决定：** **收窄**——本增量只做 `IsGenericType`（= type_params 非空，可靠）+ `IsPrimitive`。`IsGenericTypeDefinition` 延后，前置依赖 = 编译器让 `typeof` 携带实例化 type args（让运行期 Type 区分开放/实例化）。不在运行期硬塞一个不可靠的区分（符合设计完整性原则：模型不支持就停，不打补丁）。判据：IsGenericType = type_params 或 type_args 任一非空。

### Decision 3: IsPrimitive 用名集合判定

**问题：** 基元怎么认。
**决定：** 类型名匹配基元集合——keyword 形（`int`/`long`/`short`/`byte`/`sbyte`/`uint`/`ulong`/`ushort`/`float`/`double`/`bool`/`char`）+ BCL PascalCase 形（`Std.Int32`/`Std.Int64`/`Std.Boolean`/`Std.Char`/...，复用 well_known_names 常量）。`string` **非** 基元（对齐 C# `typeof(string).IsPrimitive == false`）。name-only 基元 Type（来自 typeof(int)）的 `Name` 已规范化为 keyword 形，故匹配稳定。

### Decision 4: IsClass / IsInterface / IsEnum 延后

**问题：** 是否一并做完整类别谓词。
**决定：** 延后。`IsInterface`/`IsEnum` 需类别元数据——z42 接口/枚举当前不产 TYPE 条目，运行期无从区分 name-only 接口 Type 与未知类。强行做需格式 bump（class category 字节）或脆弱的名字启发式。本增量只做能干净派生的三谓词；类别三件套入独立 format-bump 增量（Deferred）。

## Implementation Notes

- `type_handle(args)` 取 `Arc<TypeDesc>`（既有助手）；None → 谓词返 false（基元/未知 name-only Type 无 cold 时 type_params/type_args 为空 → IsGenericType false，符合预期）。
- 基元判定助手 `fn is_primitive_type_name(name: &str) -> bool`，匹配 keyword + Std.* BCL 名集合（用 well_known_names 常量 + keyword 字面量）。
- 三 builtin 注册进 `corelib/mod.rs` 表（紧邻既有 `__type_is_value_type` 等）。
- Type.z42：三个 `[Native(...)] public extern bool Xxx { get; }`（与 IsValueType / IsAbstract 同形）。

## Testing Strategy

- Golden e2e：`src/tests/types/generic_predicates.z42` —— `Box<int>`（IsGenericType true / IsGenericTypeDefinition false）、非泛型类（both false）、`int`/`bool`/`char`/`double`（IsPrimitive true）、`string`/类（IsPrimitive false）；assert-only，interp+jit。
- dotnet GoldenTests 全绿（反射回归，无格式漂移——本变更无 wire 改动，fixture 不应变）。
- cargo test --lib + xtask vm/cross-zpkg/stdlib。

## Deferred

- `reflection-future-generic-type-definition`：`IsGenericTypeDefinition` + `GetGenericTypeDefinition()`。前置 = 编译器让 `typeof(Box<int>)` 携带实例化 type args（当前 `Z42TypeName(Z42InstantiatedType)` 丢实参 → 开放/实例化运行期不可区分）。
- `reflection-future-type-category`：`IsClass` / `IsInterface` / `IsEnum`（需类别元数据，format-bump）。
