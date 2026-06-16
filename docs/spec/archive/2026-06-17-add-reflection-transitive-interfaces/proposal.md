# Proposal: 传递接口闭包（interface extends interface）

## Why

反射列表续作（完成 #2/#3 接口故事）。`interface IBar : IFoo` 的基接口 `IFoo` 当前**在 parser
就被丢弃**（`ParseInterfaceDecl` 注释 "Skip base interfaces"，InterfaceDecl AST 无对应字段）。
后果：

- `typeof(IBar).GetInterfaces()` 不含 `IFoo`（应含——传递闭包）。
- `x is IFoo` / `IsAssignableFrom` 对**经接口继承到达**的接口返 false（`class C : IBar` → `c is IFoo` 应 true）。

`interface IEnumerator<T> : IDisposable` 已在 z42.core 用着，但 `IDisposable` 拿不到——接口继承图缺失。

## What Changes

- **捕获接口基接口**：`InterfaceDecl` 加 `BaseInterfaces`；parser 由"跳过"改"捕获"。
- **codegen 填接口块**：`EmitInterfaceDesc` 把接口的基接口（FQ 名）写进接口的 TYPE 条目接口块
  （**复用 add-reflection-interface-class-predicates 的接口块结构，无 wire 新字段、无格式 bump**）。
- **runtime 传递聚合**：`__type_interfaces`（GetInterfaces）+ `is_subclass_or_eq_td`（interp `is`/`as`）
  + `is_subclass_or_eq`（JIT）对每个接口再递归其基接口。
- **无格式 bump**：仅接口条目的接口块从空变非空（同版本 reader 已能读）；需 regen stdlib + goldens。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `InterfaceDecl` 加 `List<string>? BaseInterfaces` |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | `ParseInterfaceDecl` 捕获基接口（替"Skip"） |
| `src/compiler/z42.Semantics/Codegen/IrGen.Classes.cs` | MODIFY | `EmitInterfaceDesc` 填 `Interfaces`（FQ，QualifyClassName） |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_type_interfaces` 传递 BFS（接口再展开其基接口） |
| `src/runtime/src/interp/dispatch.rs` | MODIFY | `is_subclass_or_eq_td` 接口命中后递归其基接口 |
| `src/runtime/src/jit/helpers/object.rs` | MODIFY | `is_subclass_or_eq` 同步传递查接口 |
| `docs/design/language/reflection.md` | MODIFY | 主体 + Deferred（标 transitive 落地） |
| `src/tests/types/transitive_interfaces.z42` | NEW | golden（interp+jit） |

**只读引用**：

- `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs:119-131`（现"Skip base interfaces"）
- `src/runtime/src/corelib/reflection.rs`（builtin_type_interfaces 现状）

## Out of Scope

- **泛型接口实例化的 base**（`IList<T> : ICollection<T>` 的 T 替换）：base 接口名按 bare/FQ 名传递，
  不做泛型 arg 替换，延后。
- **接口的 `IsAbstract`/方法继承**：传递只覆盖接口身份（GetInterfaces/is/IsAssignableFrom），不含
  继承接口的方法纳入 `GetMethods()`，延后。

## Open Questions

- 无（复用现有接口块，无 bump；完成 #2/#3 接口故事）。
