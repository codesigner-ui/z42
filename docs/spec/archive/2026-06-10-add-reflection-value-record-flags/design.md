# Design: Type.IsValueType / IsRecord

## Architecture

```
TypeDesc.class_flags (bit2=struct / bit3=record, 已由 add-reflection-type-flags 写入)
        │ builtin_type_is_value_type / builtin_type_is_record
        ▼
Type.IsValueType / Type.IsRecord  (extern bool getter)
```

零 wire / 编译器改动——纯读已有的 class_flags 位。

## Decisions

### Decision 1: 直接镜像 IsAbstract/IsSealed
`builtin_type_is_value_type` = `class_flag_set(args, CLASS_FLAG_STRUCT)`；`builtin_type_is_record` = `class_flag_set(args, CLASS_FLAG_RECORD)`。完全复用 `add-reflection-type-flags` 的 `class_flag_set` helper（handle-less → false）。`Type.z42` 加两个 `[Native]` extern bool getter，与 `IsAbstract`/`IsSealed` 同形。

### Decision 2: 命名 IsValueType（非 IsStruct）
C# 用 `Type.IsValueType`（值类型语义），z42 `struct` 即值类型 → 用 `IsValueType` 对齐 C#。`IsRecord` 是 z42 特有（C# record 是 class、无 Type.IsRecord），但 z42 `record` 是一等修饰符、bit 已在 wire，暴露合理。

## Implementation Notes

- builtin 二字：`("__type_is_value_type", builtin_type_is_value_type)` / `("__type_is_record", builtin_type_is_record)` 加在 `__type_is_sealed` 之后。
- `Type.z42`：`[Native("__type_is_value_type")] public extern bool IsValueType { get; }` + IsRecord，紧跟 IsSealed。

## Testing Strategy

- **Golden** `src/tests/types/type_flags.z42`：追加 struct / record / plain class 的 IsValueType/IsRecord 断言（局部接收者）。
- **Dogfood [Test]** `reflection.z42`：struct + record 类反射。
- **Rust 单测** `reflection_tests.rs`：构造带 CLASS_FLAG_STRUCT / CLASS_FLAG_RECORD 的 TypeDesc，验 builtin 返对应 bool + handle-less → false。
- **GREEN**：cargo test --lib + dotnet test（无格式 fixture 变更——不动 fixtures）。stdlib 经 driver-direct 重建 dist/release（新 builtin → 须重建 VM；Type.z42 改 → 重建 z42.core）。
- **注意**：无格式 bump → **不跑 fixture regen**，但**仍须重建 stdlib + VM**（新 builtin + Type.z42 方法），否则 golden 跑旧 VM 报 unknown builtin。
