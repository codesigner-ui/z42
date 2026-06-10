# Proposal: Type.IsValueType / IsRecord（无格式 bump）

## Why

`add-reflection-type-flags`（zbc 1.12）已**把 struct / record 位写进 wire**（TYPE section flags 字节 bit2=struct / bit3=record），但当时只暴露了 `IsAbstract` / `IsSealed`，并明确「struct/record 位已写进 wire，将来 `IsValueType`/`IsRecord` 纯 stdlib、不再 bump 格式」。本变更兑现那个设计——暴露 `Type.IsValueType`（struct → C# 值类型语义）和 `Type.IsRecord`（z42 `record` 修饰符）。

**纯 runtime + stdlib，无编译器改动、无格式 bump、不撞任何 port**（自举 zbc-writer port 已收敛）。位已在 `TypeDesc.class_flags`，只差读出来。

## What Changes

- 运行时 `reflection.rs`：`builtin_type_is_value_type`（读 `CLASS_FLAG_STRUCT`）+ `builtin_type_is_record`（读 `CLASS_FLAG_RECORD`），复用 `class_flag_set` helper。
- `mod.rs`：注册 `__type_is_value_type` / `__type_is_record`。
- `Type.z42`：`IsValueType` / `IsRecord` extern bool getter（镜像 `IsAbstract`/`IsSealed`）。
- 文档 + golden + dogfood + Rust 单测。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_is_value_type` / `builtin_type_is_record` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册两个 builtin |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsValueType` / `IsRecord` extern bool getter |
| `docs/design/language/reflection.md` | MODIFY | API 表 + Deferred 标落地（type-flags 残留项）|
| `src/tests/types/type_flags.z42` | MODIFY | golden 追加 IsValueType/IsRecord 断言 |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | dogfood [Test] |
| `src/runtime/src/corelib/reflection_tests.rs` | MODIFY | 单测（flag 解码）|

**只读引用**：`builtin_type_is_abstract`/`is_sealed` + `class_flag_set`（直接镜像）；`CLASS_FLAG_STRUCT`/`RECORD`（已定义）。

## Out of Scope

- `IsClass` / `IsInterface` / `IsEnum`：未请求；接口/枚举无 class_flags 位（要另加）。
- 任何格式变更（本变更明确零 wire 改动）。

## Open Questions

- [ ] 无。
