# Design: WeakRef builtins

## Architecture

```
Value::Object(GcRef<ScriptObject {
    type_desc: WeakHandle TypeDesc,
    slots: vec![],
    native: NativeData::WeakRef(WeakRef { ... })  ← gc::WeakRef internal
}})

[Native("__obj_make_weak")]
public static extern WeakHandle? MakeWeak(object? target);
   ▼
heap.make_weak(target) → Option<WeakRef>
   None → Value::Null
   Some(w) → alloc_object(WeakHandle TypeDesc, [], NativeData::WeakRef(w))

[Native("__obj_upgrade_weak")]
public static extern object? Upgrade(WeakHandle? handle);
   ▼
extract NativeData::WeakRef from handle's ScriptObject
heap.upgrade_weak(&w) → Option<Value>
   None → Value::Null
   Some(v) → v
```

## Decisions

### Decision 1: NativeData 加 WeakRef variant vs 单独 Value variant
**问题：** GC weak ref 怎么暴露给 z42 Value 系统？
**选项：**
- A. **NativeData::WeakRef(WeakRef)** —— 包装在 ScriptObject 内
- B. **Value::Weak(WeakRef)** —— 新 Value variant

**决定：A**。原因：
- B 影响 PartialEq / mark traversal / IR codegen 等多处
- A 复用 alloc_object 路径；只动 NativeData enum + corelib
- A 与 Pinned C5 / 未来 FileHandle 等 native-backed 类一致

### Decision 2: WeakHandle 类是普通 class 还是 PrimType
**问题：** stdlib `Std.WeakHandle` 的类型表达？
**决定：** 普通 class（z42 端 `public class WeakHandle { ... }`）。VM 端 alloc_object 用对应 TypeDesc（无字段）+ NativeData::WeakRef backing。无需 compiler 特判，与 stdlib 其他 native-backed 类一致。

### Decision 3: 错误处理 —— 类型不匹配返回 Null vs 抛错
**决定：** 返回 Null（参考 `__delegate_eq` 同款 lenient 处理）。原因：
- 用户脚本错传也能优雅退化（弱引用 dead semantic 自洽）
- 减少 try/catch 模板；reduce friction

## Implementation Notes

- `NativeData::WeakRef(crate::gc::WeakRef)` 加 variant
- `corelib/object.rs::builtin_obj_make_weak`：
  - 接 args[0]
  - `ctx.heap().make_weak(&value)` → Option<WeakRef>
  - None → Value::Null
  - Some(w) → 构造 WeakHandle TypeDesc + alloc_object 包装 NativeData::WeakRef(w)
- `corelib/object.rs::builtin_obj_upgrade_weak`：
  - 接 args[0]，必须是 Value::Object 且 native 是 WeakRef
  - 提取 WeakRef，`upgrade_weak_internal(weak)` → Option<Value>
  - None / 类型不匹配 → Value::Null
  - Some(v) → v
- WeakHandle TypeDesc 需缓存（避免每次 alloc 重建）—— 用 `OnceLock<Arc<TypeDesc>>`
- stdlib WeakHandle.z42：minimal class with `[Native]` static methods

## Testing Strategy

- VM 单元测试 `corelib/tests.rs` +5：
  - make_weak Object → 非 Null
  - make_weak Array → 非 Null
  - make_weak primitive → Null
  - upgrade alive → 原对象
  - upgrade after collect → Null（实际 Phase 1 RC 模式仅在所有强引用 drop 后才 collect，需 force_collect 触发）
- Golden test `weak_ref_basic/source.z42`：
  - 创建 Object → make weak → upgrade → 仍可访问
  - 强引用 release + force_collect → upgrade 返回 null
  - 原子值 make weak → null
- IncrementalBuildIntegrationTests 43 → 44
