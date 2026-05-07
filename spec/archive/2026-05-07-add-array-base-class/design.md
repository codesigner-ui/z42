# Design: add-array-base-class (v1 骨架)

## Architecture

```
┌─ stdlib ──────────────────────────────────────────────────┐
│ Std.Array : Object (sealed)                                │
│   public int Length { get; }     ← bridges to IR array_len │
│   public Object Clone()          ← native __array_clone    │
│   override string ToString()                               │
│   override bool Equals(Object)                             │
│   override int GetHashCode()                               │
└────────────────────────────────────────────────────────────┘
         ▲ subtype (synthetic, T[] doesn't carry TypeDesc)
         │
         ▼
[T[]] (Value::Array runtime; Z42ArrayType compile-time)

┌─ compiler ──────────────────────────────────────────────────┐
│ Z42Type.IsAssignableTo (target, Z42ArrayType source):       │
│   target == Std.Array  → true                               │
│   target == Std.Object → true (via Array's BaseClassName)   │
│   target == Z42ArrayType same elem → true (existing)        │
│   else → false                                              │
│                                                              │
│ TypeChecker.Exprs.Members:                                  │
│   arr.Length → existing FieldGet path (Value::Array branch  │
│                already exists in interp; fast path kept)    │
│   arr.Clone() → resolve to Std.Array.Clone, emit Call       │
│   arr.GetType() → resolve to Std.Object.GetType, emit Call  │
│                   (already works via Object chain once Array│
│                   is-a is wired)                            │
└─────────────────────────────────────────────────────────────┘

┌─ VM ──────────────────────────────────────────────────────────┐
│ corelib::object::builtin_obj_get_type:                        │
│   match val { Value::Object(rc) => use type_desc.name,        │
│               Value::Array(_)   => return Type{Array}, NEW    │
│               _ => bail }                                     │
│                                                                │
│ corelib::array::builtin_array_clone (NEW):                    │
│   match val { Value::Array(rc) => alloc new Array, copy slots │
│               _ => bail }                                     │
│                                                                │
│ is_subclass_or_eq_td (existing): walk BaseClass chain         │
│   Std.Array's TypeDesc has base_name = "Std.Object"           │
│   → arr `is Object` already works once Array TypeDesc loads   │
│                                                                │
│ is_instance / as_cast on Value::Array:                        │
│   if class_name in {"Std.Array", "Std.Object"} → true (NEW)   │
└────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: Std.Array 是真 stdlib 类，还是编译器内置 sentinel？

**问题**：`Std.Array` 怎样进入类型表？

- A. 在 `src/libraries/z42.core/src/Array.z42` 写真 z42 类，走标准 stdlib 加载路径
- B. 编译器侧 SymbolCollector 启动时合成 `Z42ClassType("Std.Array", ...)` 注入到 `_classes`

**决定**：选 **A**。

理由：
1. 与 `Std.Object` / `Std.Exception` 等其他 Object 协议根类一致 —— 都是 z42 源码 + native bindings
2. SymbolCollector 不引入特殊路径
3. 用户读 `Array.z42` 能看到完整签名（Length / Clone / 继承自 Object）
4. 测试覆盖一致（普通 stdlib 测试机制）

### Decision 2: `T[]` 真子类型化 `Std.Array` 的实现位置

**问题**：`Z42ArrayType` 不是 `Z42ClassType`。怎么让 TypeChecker 把 `T[]` 视为 `Std.Array` 子类？

**决定**：在 [`Z42Type.cs::IsAssignableTo`](../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) 加专门分支：

```csharp
// Source 是 Z42ArrayType 时，target 可以是：
//   1. 同 element 的 Z42ArrayType（现有路径）
//   2. Z42ClassType("Std.Array")  ← NEW
//   3. Z42ClassType("Std.Object") ← NEW (Array → Object 链)
if (source is Z42ArrayType srcArr) {
    if (target is Z42ArrayType tgtArr && IsAssignableTo(tgtArr.Element, srcArr.Element)) return true;
    if (target is Z42ClassType tgtCls
        && (tgtCls.Name == "Array" || tgtCls.Name == "Object")) return true;
    return false;
}
```

替换原 `Z42Type.cs:96` 处的 catch-all（仅针对 Z42ArrayType；primitive 走原路径）。

### Decision 3: VM `is_instance` for `Value::Array`

**问题**：runtime `xs is Array` 怎样工作？

**决定**：在 `is_instance` / `as_cast` 指令的 VM 端，对 `Value::Array` 接收者做 hardcoded 名字比较：`class_name in {"Std.Array", "Std.Object"}` → true。

理由：
- Value::Array 没有 TypeDesc，不能走通用 `is_subclass_or_eq_td` 路径
- 硬编码两个特定名字简单、显式；后续若加 `Std.Array<T>` 元素类型反射时再扩展
- 与 `Value::Str` 已有的 hardcoded `is "Std.String"` 模式一致

### Decision 4: `arr.Length` 路径

**问题**：保留 IR `array_len` 快路径，还是走虚分发到 `Std.Array.get_Length`？

**决定**：保留快路径。

理由：
- 现有 `Value::Array` `FieldGet` 已经 hardcoded 处理 `"Length"` / `"Count"` 返回 i64
- TypeChecker 看 `arr.Length`（receiver 静态类型 = Z42ArrayType）时，emit `FieldGet`（与现状一致）
- 若 receiver 静态类型已经"upcast"到 `Std.Array`（如 `Array a = xs; int n = a.Length`），需要在 BindMember 加判断：if `Std.Array` 类的 receiver 实际可能是 `Value::Array`，emit `FieldGet` 而非 `VCall`
- 实现：`TypeChecker.Exprs.Members.cs` 检测 `member.Name == "Length"` && (`receiver.Type is Z42ArrayType` || `receiver.Type is Z42ClassType { Name == "Array" }`) → 走 FieldGet 路径

### Decision 5: `arr.Clone()` emit

**问题**：emit 什么 IR 调用？

**决定**：emit `Builtin "__array_clone"`（已有 builtin 通道；不新增 IR opcode）。

理由：
- `Std.Array.Clone` body 在 z42 源码里写 `[Native("__array_clone")] public extern Object Clone();`
- TypeChecker 看到 `arr.Clone()` 解析到 `Std.Array.Clone` → 走标准 native 调用路径
- IR / VM 都不需要为此专门加路径

### Decision 6: `Std.Array.Length` 的 z42 声明形式

**问题**：声明为 property（带 `get;` accessor）还是普通字段？z42 暂无完整 property syntax。

**决定**：声明为字段：`public int Length;`，借助现有 FieldGet hardcoded `"Length"` 路径。无 setter（语义上 read-only，由 VM 端不接受 FieldSet on array 兜底；本 spec 不强化此约束）。

理由：
- z42 现有 stdlib 类如 `Std.MulticastException.Failures` 等都用 plain field 声明
- IR FieldGet 直查 field_name；与 `Z42ArrayType` 的 hardcoded "Length" 字段对齐
- v1 简洁；property syntax 是独立的语言特性 follow-up

### Decision 7: `Std.Array` 的内部表示

**问题**：`Std.Array` 类的 ScriptObject 槽位上要不要有真实数组数据？

**决定**：不需要。`Std.Array` 是**虚类**：
- 类的 `Fields` 列表里声明 `int Length`（用于符号表 + IR FieldGet 解析）
- 但**没有 ScriptObject 实例承载** —— 用户不能 `new Array()`（sealed + 无 public ctor）
- runtime 看到 `arr is Array` / `arr.GetType()` 时返回常量 Type info
- `Length` access 走 IR `array_len`（不进 ScriptObject slot）

实现层面：
- `Std.Array.z42` 类标 `sealed`，无 public ctor（用 private ctor 阻止用户构造）
- Length 字段声明只起符号占位作用

## Implementation Notes

### Std.Array.z42

```z42
namespace Std;

// 2026-05-07 add-array-base-class:
// 所有 T[] 在运行时是 Std.Array 的实例（编译器特殊处理 is-a；
// Value::Array 不携带 TypeDesc 引用，VM 硬编码识别 Std.Array / Std.Object 子类型）。
public sealed class Array {
    // 字段占位：实际值由 IR array_len 直读；用户写 arr.Length 时 TypeChecker
    // 把它解析为 FieldGet（Value::Array 在 interp 已 hardcoded 此分支）。
    public int Length;

    // private ctor 防止用户 `new Array()`；T[] 的"构造"通过 array_new IR 指令。
    private Array() { }

    [Native("__array_clone")]
    public extern Object Clone();
}
```

### corelib/array.rs (新文件)

```rust
pub fn builtin_array_clone(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    if args.len() != 1 {
        bail!("__array_clone: expected 1 argument, got {}", args.len());
    }
    match &args[0] {
        Value::Array(rc) => {
            let copy = rc.borrow().clone();
            Ok(Value::Array(GcRef::new(copy)))
        }
        other => bail!("__array_clone: expected array, got {:?}", other),
    }
}
```

### corelib/object.rs `builtin_obj_get_type`

```rust
pub fn builtin_obj_get_type(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    match &args[0] {
        Value::Object(rc) => /* existing path: build Type from rc.borrow().type_desc */,
        Value::Array(_)   => /* NEW: build Type with __name="Array", __fullName="Std.Array" */,
        Value::Str(_)     => /* existing: __name="String" */,
        _                 => bail!("expected an object"),
    }
}
```

### TypeChecker.Exprs.Members.cs

case `arr.Length`：当 receiver 类型是 Z42ArrayType **或** is `Std.Array` 类，且 member name 是 "Length"：
- emit BoundMemberAccess (会走到 IrGen 的 FieldGet path)
- BoundMemberAccess.Type = Z42Type.Int
- 不去 Std.Array.Methods 里查方法

case `arr.Clone()`：当 receiver 类型是 Z42ArrayType 或 Std.Array：
- 解析到 `Std.Array.Clone` 方法（在 imported.Classes["Array"].Methods["Clone"]）
- 走 BoundCall 路径，emit Call (or Builtin) → `Std.Array.Clone` → IrGen 因为有 [Native] 注解走 builtin call 通道

case `arr.GetType()` / `arr.Equals(o)` / `arr.ToString()` / `arr.GetHashCode()`：
- receiver 是 Array → 解析到 Std.Array → 继承自 Std.Object 的 method → 走 Object 协议 builtin（`__obj_get_type` / `__obj_equals` / `__obj_to_str` / `__obj_hash_code`）

## Testing Strategy

- xUnit `ArrayBaseClassTests.cs`:
  - `T[] is-a Std.Array` (IsAssignableTo)
  - `T[] is-a Std.Object` (chain)
  - `arr.Length` emits FieldGet
  - `arr.Clone()` resolves to Std.Array.Clone
  - Std.Array sealed (negative test: derive class fails)
- Golden `array_get_type/`: `arr.GetType().__fullName` 输出 `"Std.Array"`
- Golden `array_clone/`: 浅拷贝 primitive vs ref 元素验证
- Existing array tests (`24_arrays` 等) 不破

## Out of Scope Reminders

- 静态算法 / IEnumerable / 协变 / element type metadata —— 后续独立 spec
- Multi-dim / jagged array 基类讨论
- JIT path 的 array `is_instance` / GetType 同款 —— 与 interp 镜像，本 spec 一并落地以保证 jit/interp 平价
