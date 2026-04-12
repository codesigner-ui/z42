# Proposal: Script Object Type and String Members

## Why

当前 VM 对象系统存在三个根本缺陷，制约了 stdlib 成员变量的正确表达：

1. **字段用 `HashMap<String, Value>`** — O(n) hash 查找，字段名留在运行时，无法为 JIT 生成直接偏移访问
2. **虚方法每次调用走继承链** — O(depth) 线性扫描，而 CoreCLR 用预计算 vtable 做到 O(1)
3. **String/Array 是孤立基元** — `s.Length` 需要 IrGen hardcode 特判（`builtin "__len"`），`__sb_append` 等 StringBuilder 内建靠 `fields["__data"]` 这个暴露给脚本的魔法字段维持状态

参照 CoreCLR 的 MethodTable + 内联字段设计，引入 **ScriptObject** 作为统一托管对象类型，
使 `string.Length` 成为真正的虚拟字段访问，并为所有 stdlib 类型提供统一的 `NativeData` backing 机制。

## What Changes

- **VM `types.rs`**: 新增 `TypeDesc`（预构建类型描述符，`Arc` 共享）、`FieldSlot`、`NativeData`、`ScriptObject`；`ObjectData` → `ScriptObject`
- **VM `loader.rs`**: 加载时预构建 `TypeDesc` 注册表（继承链展平 + vtable 预计算）
- **VM `interp/mod.rs`**: `ObjNew`/`FieldGet`/`FieldSet`/`VCall`/`IsInstance`/`AsCast` 迁移到 `ScriptObject`；`FieldGet` 对 `Value::Str` 做虚拟字段派发（`Length`、`IsEmpty`）
- **VM `corelib/`**: StringBuilder builtins 读写 `NativeData::StringBuilder`，不再访问 `fields["__data"]`
- **Parser `TopLevelParser.cs`**: 支持 `extern` 属性 `{ get; }` / `{ get; set; }` 语法
- **Stdlib `String.z42`**: 新增 `Length` 和 `IsEmpty` extern 属性声明
- **Stdlib `StringBuilder.z42`**: 移除 `private string __data` 字段
- **IrGen `IrGenExprs.cs`**: 移除 `Length`/`Count` hardcode 特判（两处），走正常 `field_get` 路径

## Scope（允许改动的文件）

| 文件/模块 | 变更类型 |
|-----------|---------|
| `src/runtime/src/metadata/types.rs` | 新增类型 |
| `src/runtime/src/metadata/loader.rs` | 新增 TypeDesc 构建 |
| `src/runtime/src/metadata/bytecode.rs` | ClassDesc 保留（IR 层不变）|
| `src/runtime/src/interp/mod.rs` | 迁移 Object 操作 |
| `src/runtime/src/corelib/collections.rs` | StringBuilder NativeData |
| `src/runtime/src/corelib/object.rs` | 适配 ScriptObject API |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | extern 属性语法 |
| `src/compiler/z42.Semantics/Codegen/IrGenExprs.cs` | 移除 hardcode |
| `src/libraries/z42.core/src/String.z42` | 新增属性声明 |
| `src/libraries/z42.text/src/StringBuilder.z42` | 移除 __data |

## Out of Scope

- Array/Map 装箱为 ScriptObject（性能敏感，L3）
- `string` 索引器 `s[i]` → `char`（下一轮）
- JIT 字段偏移直接访问（整数 offset）
- `NativeData::ListBuffer` / `MapBuffer`
- 写属性 `{ get; set; }` 的 setter 路由（本轮只用 getter）

## Open Questions

- [x] vtable 用 `HashMap<String, String>` 还是 `Vec` + index？→ 采用 `Vec<(String,String)>` + `HashMap<String,usize>` 保留 slot index 语义
- [x] `IsEmpty` 在 VM 层还是脚本层？→ 脚本层（`String.z42` 中用 `Length == 0` 实现）
