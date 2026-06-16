# Proposal: Type.IsClass / Type.IsInterface

## Why

反射列表 #2（IsEnum 部分延后）。z42 当前缺 `Type.IsClass` / `Type.IsInterface`
类别谓词。`IsClass` 可从已有 `class_flags` 直接推导（零成本）；但 **interface 当前完全
不产 TYPE section 条目**（`IrGen.Generate.cs` 只迭代 `cu.Classes`），故 `typeof(IFoo)`
是 name-only synthetic Type（无 handle），既无法 `IsInterface`，连 `Name`/`IsAbstract`
也读不到。让接口产**最小 ClassDesc**（带 interface 类别 flag）是 `IsInterface` 的前置，
也是接口可反射化的基础改善。

`IsEnum` 延后：enum 在 z42 底层只是 int 常量字典、无类型实体，需先做"enum 作为类型实体"
的类型系统设计（独立 change + design doc）。

## What Changes

- **IsClass**（零格式 bump，纯运行期）：`has handle && !struct_bit && !interface_bit`
  （记录是 class → true；接口/枚举/基元/数组 → false）。
- **接口产最小 TYPE 条目**：`IrGen` 为每个 `InterfaceDecl` emit 一个 `IrClassDesc`
  （`IsInterface=true`、`IsAbstract=true`、无 base、无字段、带 TypeParams）。
- **flags 字节扩位**：`class_flags` bit4 = interface（bit0-3 已用 abstract/sealed/struct/record）。
- **zbc 1.18→1.19 / zpkg 0.20→0.21**（flags 语义扩展 = wire 变更）。
- **stdlib**：`Std.Type.IsClass` / `IsInterface` extern。
- **z42c writer 同步延后**（z42c 锁被占，沿用先例）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | `IrClassDesc` 加 `bool IsInterface = false` |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `EmitInterfaceDesc(InterfaceDecl)` |
| `src/compiler/z42.Semantics/Codegen/IrGen.Generate.cs` | MODIFY | `classes` 追加 `cu.Interfaces.Select(EmitInterfaceDesc)` |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | **根因修（Scope 扩展）**：`Z42TypeName` 加 `Z42InterfaceType` 分支 → `QualifyClassName`。接口现产 TYPE 条目（FQ 名），typeof(IFoo) 必须限定否则 make_type_from_name 漏句柄、IsInterface 退化 false |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | flags byte `\| (IsInterface ? 16 : 0)`；`VersionMinor` 18→19 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 读 flags bit4 → `IrClassDesc.IsInterface`（round-trip） |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 20→21 |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `CLASS_FLAG_INTERFACE: u8 = 1 << 4` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR` 19 / `ZPKG_VERSION_MINOR` 21 |
| `src/runtime/src/metadata/zbc_reader_tests.rs` | MODIFY | version-pin 19/21 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_is_class` / `builtin_type_is_interface` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 2 新 builtin |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsClass` / `IsInterface` extern |
| `docs/design/language/reflection.md` | MODIFY | 主体节 + Deferred（IsEnum 续作） |
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog 1.19 |
| `docs/design/runtime/zpkg.md` | MODIFY | Minor changelog 0.21 |
| `src/tests/types/interface_class_predicates.z42` | NEW | golden（interp+jit） |
| `src/tests/zbc-format/**`（regen） | MODIFY | fixture regen |
| `src/tests/zpkg-format/**`（regen） | MODIFY | fixture regen |

**只读引用**：

- `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs:30-66`（EmitClassDesc 镜像）
- `src/compiler/z42.Syntax/Parser/Ast.cs:85`（InterfaceDecl 结构）
- `.claude/rules/version-bumping.md`

## Out of Scope

- **IsEnum**：需 enum 类型实体设计（独立 change + design doc）。
- **接口成员枚举**：`typeof(IFoo).GetMethods()` 返回接口方法 —— 最小条目不含方法表，延后。
- **接口继承接口**（`interface IBar : IFoo`）→ 传递接口闭包：InterfaceDecl 无 base-interface 列表，延后。
- **数组 IsClass = true**（C# 语义）：z42 数组是 name-only synthetic（无 handle）→ IsClass false，延后。
- **z42c writer 同步**：follow-up（z42c 锁被占）。

## Open Questions

- 无（范围已与 User 确认：IsClass + IsInterface，IsEnum 延后）。
