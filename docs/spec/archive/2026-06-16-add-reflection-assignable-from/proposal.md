# Proposal: Type.IsAssignableFrom / GetInterface + FQ 接口身份

## Why

反射列表 #3。`IsAssignableFrom(Type)` 是反射版的 `is`/赋值兼容判断。**正确实现必须在 VM
里用真实类型身份比较**（`TypeDesc` base 链 + 接口），而非比较合成 `Type` 对象的字符串名
（脆、跨命名空间同名误匹配）。

实现中暴露**两个根因缺陷**，本变更一并修：

1. **接口在 wire 上只存短名**（`TypeDesc.interfaces` = bare `"IShape"`，add-reflection-get-interfaces
   为省成本定）→ 接口身份无法 robust 比较，`GetInterfaces()` 只能返 name-only Type。
2. **VM 的 `is_subclass_or_eq_td`（`x is`/`as` 的权威判定）只走 base_name 类链，不查接口**
   → `circle is IShape` 现在返回 **false**（接口的 `is` 一直没真正支持）。

## What Changes

- **接口块改存 FQ 名**（`IrGen` 把接口名 `QualifyClassName` 后写入）→ 接口身份可 robust 比较，
  `GetInterfaces()` 返回**真接口句柄**。zbc 1.19→1.20 / zpkg 0.21→0.22。
- **`is_subclass_or_eq_td` 扩展查接口**（base 链每层比较声明的接口 FQ 名）→ `x is IShape` /
  `as IShape` 对接口**真正生效**（修既有 false）。
- **`IsAssignableFrom(Type)` native builtin**：`__type_is_assignable_from(this, other)` 复用
  `is_subclass_or_eq_td`（真实 TypeDesc FQ 名，类链 + 接口都 robust）。
- **`GetInterface(string)`**：纯 z42，遍历 `GetInterfaces()` 按名匹配（输入即名字，合理）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `InterfaceTypeName` → `QualifyClassName`（接口块存 FQ 名） |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor` 19→20 + 注释（接口名 bare→FQ 语义变化） |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 21→22 |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR` 20 / `ZPKG_VERSION_MINOR` 22 |
| `src/runtime/src/metadata/zbc_reader_tests.rs` | MODIFY | version-pin 20/22 |
| `src/runtime/src/interp/dispatch.rs` | MODIFY | `is_subclass_or_eq_td` base 链每层查接口 FQ 名（interp `is`/`as`） |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | `is_subclass_or_eq`（JIT `is`/`as` 镜像）同样查接口 |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_is_assignable_from`（复用 is_subclass_or_eq_td） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__type_is_assignable_from` |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsAssignableFrom(Type)` extern + `GetInterface(string)` z42 |
| `docs/design/language/reflection.md` | MODIFY | 主体 + API 表 + Deferred |
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog 1.20 |
| `docs/design/runtime/zpkg.md` | MODIFY | Minor changelog 0.22 |
| `src/tests/types/assignable_from.z42` | NEW | golden（interp+jit） |
| `src/tests/zbc-format/**`（regen） | MODIFY | fixture regen |
| `src/tests/zpkg-format/**`（regen） | MODIFY | fixture regen |

**只读引用**：

- `src/runtime/src/interp/exec_object.rs`（is_instance / as_cast 调用点）
- `src/runtime/src/corelib/reflection.rs:48-78`（make_type_from_name；FQ 名 → 真句柄）
- `.claude/rules/version-bumping.md`

## Out of Scope

- **传递接口闭包**（`interface IBar : IFoo` → 含 IFoo）：InterfaceDecl 无 base-interface 列表，延后。
- **泛型变体**（`IList<Derived>` → `IList<Base>` 协变）：反射 MVP 不含。
- **handle-less 类型的 IsAssignableFrom**（`object.IsAssignableFrom(int)` 装箱语义）：基元/数组
  name-only 无 base 链 → 退化为同名判断；C# 装箱语义延后。
- **z42c writer 同步**：follow-up（z42c 锁被占）。

## Open Questions

- 无（方案已与 User 确认：VM 真实比较 + FQ 接口名 + zbc 1.20）。
