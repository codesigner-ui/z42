# Proposal: GetFields() 含静态字段 + FieldInfo.IsStatic（zbc 1.13）

## Why

`Type.GetFields()` 当前只返回**实例**字段（`TypeDesc.fields`，热路径布局）。C# `GetFields()` 默认含 public 静态字段；z42 反射拿不到。根因：编译期 `EmitClassDesc` 用 `cls.Fields.Where(!IsStatic)` **过滤掉静态字段**，运行期只有 `VmContext.static_fields`（全局 Vec，按名 key，**无 per-type 类型元数据**）。所以要把静态字段的 (名, 类型) 持久化进 zbc TYPE section。

zbc 格式变更（TYPE section 每类追加静态字段块）→ **zbc 1.12→1.13 / zpkg 0.14→0.15**。`port-z42c-zbc-writer` 仍在 mid-re-port（对齐 1.12）→ 顺势对齐 1.13，同周期不多一轮（User 已选「按顺序推进」）。

## What Changes

- zbc TYPE section 每类在 flags 字节之后追加 `static_field_count: u16` + 每条 (`name_str_idx: u32`, `type_tag: u8`, `type_str_idx: u32`)——与实例字段同形。
- `IrClassDesc` 加 `List<IrFieldDesc> StaticFields`；`IrGen.EmitClassDesc` 从 `cls.Fields.Where(IsStatic)` 填充（**不**并入实例 `Fields`，保持热路径实例布局纯净）。
- 运行期 `ClassDesc.static_fields` + `TypeDescCold.static_fields`（cold，反射专用）；`build_type_registry` 透传。
- `builtin_type_fields` 在实例字段之后追加静态字段，每个 `FieldInfo` 写 `IsStatic`（实例 false / 静态 true）。
- `FieldInfo` 加 `bool IsStatic`。
- 版本 bump 全套 + fixture regen + stdlib regen + 文档 + golden + dogfood + Rust 单测。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrClassDesc.StaticFields` |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `EmitClassDesc` 填 StaticFields |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | TYPE section 写静态字段块 + `VersionMinor 12→13` |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | `ReadTypeSection` 读静态字段块 |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor 14→15` |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `ClassDesc.static_fields: Box<[FieldDesc]>` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `read_type` 读静态字段 + 版本常量 13/15 |
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeDescCold.static_fields` |
| `src/runtime/src/metadata/loader.rs` | MODIFY | `build_type_registry` 透传 static_fields（含 cold 分配判定）|
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_fields` 追加静态 + `IsStatic` 槽 |
| `src/libraries/z42.core/src/Reflection/FieldInfo.z42` | MODIFY | `public bool IsStatic` |
| `docs/design/runtime/zbc.md` | MODIFY | 1.13 minor changelog |
| `docs/design/runtime/zpkg.md` | MODIFY | 0.15 minor changelog |
| `docs/design/language/reflection.md` | MODIFY | API + 实现原理 + Deferred 标落地 |
| `src/tests/types/static_fields_reflect.z42` | NEW | golden |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | dogfood [Test] |
| `src/runtime/src/corelib/reflection_tests.rs` | MODIFY | 单测 |
| `src/tests/zbc-format/generate-fixtures.sh` | RUN | regen zbc fixtures |
| `src/tests/zpkg-format/generate-fixtures.sh` | RUN | regen zpkg fixtures |

**只读引用**：实例字段 emit/read 路径（同形镜像）；`build_method_info`/`builtin_type_fields` 现状。

## Out of Scope

- **继承的静态字段**：MVP 仅返回**声明类自身**的静态字段（实例字段经 cross-zpkg fixup 含继承；静态字段不走该 fixup）。继承静态反射记 Deferred。
- `FieldInfo.GetValue`/`SetValue`（需 0.5.x）。
- const / readonly 修饰符反射。

## Open Questions

- [ ] 无（设计闭合，见 design.md）。
