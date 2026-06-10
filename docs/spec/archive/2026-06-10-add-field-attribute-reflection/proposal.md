# Proposal: 字段级用户 attribute 反射（zbc 1.14）

## Why

用户自定义 attribute 现可标注**类**（C3a）和**方法/函数**（C3b），经 `Type` / `MethodInfo` 反射。但**字段**还不行——`class C { [Json] public int x; }` 解析时 attribute 被丢弃。补齐字段目标，经 `FieldInfo.GetCustomAttributes()` / `GetAttribute(Type)` 反射活实例。

机制完全复用 C3a/C3b 的 factory-thunk：编译期合成无参工厂 `() => new Foo(args)`，wire 记 (attr-type, factory) 引用，运行期调工厂构造活实例 + 缓存。字段 attr 引用进 zbc TYPE section 的字段记录 → **zbc 1.13→1.14 / zpkg 0.15→0.16**。

> **Scope = 字段；参数 attribute 是紧邻 follow-up**：参数无 per-param wire 元数据（`ParameterInfo` 现由函数签名运行期构造），加参数 attr 需在 SIGS 段新增 per-param attr 块，基础设施不同、更大——拆为 `add-param-attribute-reflection`（见 Out of Scope）。

## What Changes

- `FieldDecl` 加 `List<AttributeApp>? Attributes`；parser 把已累积的 `pendingUserAttrs` 附到 `FieldDecl`（现为丢弃）而非 method-only。
- `AttributeFactorySynthesizer.ProcessClass` 遍历字段，为每个字段 attribute 合成工厂（key `fld$<Class>$<Field>`），复用 `ProcessAttributes`。
- `IrFieldDesc` 加 `List<IrAttributeRef>? Attributes`；`EmitClassDesc` 填充（实例 + 静态字段都支持）。
- zbc TYPE section 每个字段记录（实例块 + 静态块）在 `type_str` 之后追加 `attr_count: u16` + (type-name u32, factory u32) 对。
- 运行期 `FieldDesc.attributes`；`builtin_field_custom_attributes(qualified_field)` 调工厂；`FieldInfo` 携 `__qualified`（`<Class>.<Field>`）+ `GetCustomAttributes()` / `GetAttribute(Type)`（镜像 MethodInfo）。
- 版本 bump + fixture + stdlib regen + 文档 + golden + dogfood + Rust 单测。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `FieldDecl.Attributes` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | 字段分支附 `pendingUserAttrs`（现丢弃）|
| `src/compiler/z42.Semantics/Codegen/AttributeFactorySynthesizer.cs` | MODIFY | `ProcessClass` 遍历字段合成工厂 |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrFieldDesc.Attributes` |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `EmitClassDesc` 填字段 attr refs（实例 + 静态）|
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | 字段记录写 attr refs + intern + `VersionMinor 13→14` |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 字段记录读 attr refs |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor 15→16` |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `FieldDesc.attributes: Box<[AttributeRef]>` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `read_type` 字段读 attr refs + 版本常量 14/16 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_field_custom_attributes` + `build_field_info` 加 `__qualified` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__field_custom_attributes` |
| `src/libraries/z42.core/src/Reflection/FieldInfo.z42` | MODIFY | `__qualified` + `GetCustomAttributes()` / `GetAttribute(Type)` |
| `docs/design/runtime/zbc.md` | MODIFY | 1.14 changelog |
| `docs/design/runtime/zpkg.md` | MODIFY | 0.16 changelog |
| `docs/design/language/attributes.md` | MODIFY | field target 落地 + Deferred 更新 |
| `docs/design/language/reflection.md` | MODIFY | FieldInfo attr API |
| `src/tests/attributes/field_attrs.z42` | NEW | golden |
| `src/libraries/z42.core/tests/reflection.z42` | MODIFY | dogfood [Test] |
| `src/runtime/src/corelib/reflection_tests.rs` | MODIFY | 单测 |
| `src/tests/zbc-format/generate-fixtures.sh` | RUN | regen zbc fixtures |
| `src/tests/zpkg-format/generate-fixtures.sh` | RUN | regen zpkg fixtures |

**只读引用**：C3a/C3b attr 机制（`InternAttributeRefs`、`call_attribute_factories`、`build_method_info` 的 `__qualified`/`GetCustomAttributes` 镜像）。

## Out of Scope

- **参数 attribute**（`add-param-attribute-reflection`）：需 SIGS 段 per-param attr 块 + parser 在参数列表解析 `[Attr]` + `ParameterInfo.GetCustomAttributes`。基础设施不同、更大，拆独立 change。
- `AttributeUsage` target 校验（attribute 可标任意支持位）。
- field/param 之外的目标（property / return / assembly 等）。

## Open Questions

- [ ] 无（设计闭合，见 design.md）。
