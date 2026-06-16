# Proposal: 实例泛型 args —— obj.GetType().GetGenericArguments()

## Why

反射列表续作（接续 #1 add-reflection-generic-type-definition）。`typeof(Box<int>)` 侧已能返回
实例化 args（#1），但 **`new Box<int>()` 实例的 `obj.GetType().GetGenericArguments()` 仍返空**——
两条泛型反射路径只完成了一半。

实例的构造 args 在运行期**已经存在**（`ScriptObject.type_args`，由 ObjNew 在分配时写入），只是
`obj.GetType()`（`__obj_get_type`）没把它挂到返回 Type 的 `__typeArgs` 槽。补这一步即完成实例侧。

## What Changes

- **`__obj_get_type`**：当对象是泛型实例（`ScriptObject.type_args` 非空）时，构建**构造型** Type
  （复用 #1 的 `make_constructed_type`，把实例 args 挂到 `__typeArgs` 槽），而非裸 `make_type_object`。
- **无格式 bump、无 compiler / stdlib 改动**——纯运行期；`GetGenericArguments()` 已读 `__typeArgs` 槽（#1）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/corelib/object.rs` | MODIFY | `builtin_obj_get_type` Object 分支：type_args 非空 → make_constructed_type |
| `docs/design/language/reflection.md` | MODIFY | 主体节补实例侧 + Deferred（标 instance-generic-args 落地） |
| `src/tests/types/instance_generic_args.z42` | NEW | golden（interp+jit） |

**只读引用**：

- `src/runtime/src/corelib/reflection.rs:91`（make_constructed_type，#1）
- `src/runtime/src/metadata/types.rs:344`（ScriptObject.type_args 访问器）

## Out of Scope

- **嵌套泛型实例**（`new Box<Map<K,V>>()`）：arg 仍是扁平名串，递归解析延后（同 #1 nested-generic-args）。
- **IsGenericTypeDefinition on instance**：实例总是构造型（type_args 非空）；定义型经 GetGenericTypeDefinition 取。

## Open Questions

- 无（纯运行期，接续 #1 的槽机制）。
