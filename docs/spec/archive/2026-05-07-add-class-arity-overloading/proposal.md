# Proposal: add-class-arity-overloading — class registry arity-aware

## Why

z42 当前 class registry `_classes` 用裸 simple name（`cls.Name`）作 key，**不区分 arity**。同名 class 的非泛型 / 泛型版本（`MulticastException` 与 `MulticastException<R>`）冲突，编译期报 `E0408 duplicate` + `E0411 sealed`。

Delegate registry 早就是 arity-aware（`Action$1` / `Func$2`），class 没跟进。这是 D-8b-0（前置阻塞 D-8b-1 stdlib `MulticastException<R>` + D-8b-3 Phase 2 `default(R)` 的真泛型解析）。

来源：[docs/deferred.md](docs/deferred.md) D-8b-0。

## What Changes

- `Z42ClassType` 增 `IrName` 派生属性：`$"{Name}${TypeParams.Count}"` if generic else `Name`
- SymbolCollector class 注册改为以 IrName 为 key — 非泛型 `Foo` 占 `"Foo"`，泛型 `Foo<R>` 占 `"Foo$1"`，二者共存
- `ResolveType` 路由：
  - `NamedType("X")` → `_classes["X"]`（arity 0 槽位）
  - `GenericType("X", args)` → `_classes["X${args.Count}"]`
- IrGen / TSIG / zbc / VM 全链路按 IrName emit / lookup（**所有**泛型 class，不止 collision 场景；arity-suffix 一致性强于压缩名空间）
- 这意味着 stdlib 现有泛型类（`List<T>` / `Dictionary<K,V>` / `MulticastAction<T>` / `MulticastSubscription<T>` / `MulticastFunc<T,R>` / `KeyValuePair<K,V>` / `LinkedList<T>` / `LinkedListNode<T>` 等）的 **IR 名变 `Foo$N`**，需要 regen 全部 zpkg
- 用户不感知此 mangling — 源代码里仍写 `List<int>` / `MyClass<T>`，诊断 / `typeof` / 错误信息仍显示 user-friendly name

## Out of Scope（本变更不做）

- **类方法层面 generic-vs-non-generic 同名重载**：`class Foo { void m(); void m<T>(); }` 当前由方法 arity overload 已支持（按 arity 区分），本变更不重新设计方法 registry
- **跨 arity hierarchy continuity**：`class Foo<R>` 的 `Foo` 是否可被 `class Foo` 继承类树检查到 — 不做（两者是独立类型，不可互相 BaseClass）
- **`MulticastException<R>` stdlib 类本身**：D-8b-1 单独 spec；本变更只解锁注册层
- **`default(R)` 泛型 type-param 解析**：D-8b-3 Phase 2 单独 spec；本变更不引入 IR `DefaultOf` opcode
- **方法层面 same-name + same-arity by modifier overload**：(D-8b-2 / ref-out-in 引入 `Name$Arity$<modSig>`)，与本变更正交

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| [src/compiler/z42.Semantics/TypeCheck/Z42Type.cs](src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) | MODIFY | `Z42ClassType` 增 `IrName` 派生属性 |
| [src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs](src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs) | MODIFY | class 注册 / lookup 全部改用 IrName key（前 / 后置 pass / inheritance merge）|
| [src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs](src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs) | MODIFY | ResolveType GenericType 分支 → 用 `Name$N` lookup |
| [src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs](src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs) | MODIFY | 同上（SymbolTable 镜像 ResolveType） |
| [src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs](src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs) | MODIFY | imported class 注册按 IrName key（保持与 declaring zpkg 一致）|
| [src/compiler/z42.Semantics/Codegen/IrGen.cs](src/compiler/z42.Semantics/Codegen/IrGen.cs) | MODIFY | class IR 名 / FQ 名 emit 用 IrName |
| [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs](src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs) + Calls / Exprs | MODIFY | ObjNew / FieldGet / VCall / IsInstance / CallInstr 接收类名走 IrName |
| [src/compiler/z42.IR/ExportedTypes.cs](src/compiler/z42.IR/ExportedTypes.cs) | 只读 | 已有 TypeParams 字段；IrName 由消费方派生 |
| [src/runtime/src/metadata/loader.rs](src/runtime/src/metadata/loader.rs) + zbc_reader | MODIFY | 透明 — class.name 字段已是字符串；IrName 即 .name |
| [src/runtime/src/interp/dispatch.rs](src/runtime/src/interp/dispatch.rs) `is_subclass_or_eq_td` | 只读 | 透明 — 已用 type_registry 字符串 lookup |
| `src/tests/classes/arity_overloading/` | NEW | golden：`class Foo` + `class Foo<R>` 共存 + new + ToString |
| `src/tests/classes/arity_method_dispatch/` | NEW | golden：generic class 方法 dispatch 通过 mangled name 正常 |
| [src/compiler/z42.Tests/ClassArityOverloadingTests.cs](src/compiler/z42.Tests/ClassArityOverloadingTests.cs) | NEW | C# 单测 — 注册 / 解析 / IrName 派生 |
| stdlib zpkg | regen | 通过 `regen-golden-tests.sh`（全 stdlib 重编译）覆盖 |
| [docs/design/generics.md](docs/design/generics.md) | MODIFY | 加 "class arity overloading" 段说明 IrName + registry key 设计 |
| [docs/deferred.md](docs/deferred.md) | MODIFY | D-8b-0 移到"已落地"，D-8b-1 / D-8b-3 Phase 2 标记前置已解除 |

## Open Questions

无（关键决策已在 deferred 中钉死：使用 `Name$N` mangling 与 delegate 一致）。
