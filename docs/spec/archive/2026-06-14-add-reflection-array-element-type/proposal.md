# Proposal: 数组元素类型反射 — 运行期不擦除（Type.IsArray / GetElementType）

## Why

`int[]` 当前在运行期**类型擦除**：`Value::Array` 是裸 `GcRef<Vec<Value>>`，不记元素类型；`typeof(int[])` 也只 emit `"Std.Array"`。所以 `arr.GetType().GetElementType()` 拿不到 `int`。对齐 C#：数组在运行期**携带元素类型**，反射可还原（`typeof(int[])`、字段/参数 `int[]`、`arr.GetType()` 四条路一致）。

**User 裁决（2026-06-11）**：不接受擦除——运行期必须知道元素类型；用统一 `Type` + 能力查询建模（方案 A，与现有 `GetGenericArguments()`-on-`Type` 一致），可改 VM + 编译器。

## What Changes

**核心：数组值在运行期携带元素类型**（消除擦除）：
- 数组堆 payload `Vec<Value>` → `ArrayObj { element_type: Arc<str>, elems: Vec<Value> }`（`Deref/DerefMut/Index` 委托 `.elems`，把消费点改动降到最小）。
- `ArrayNew` / `ArrayNewLit` 加「元素类型名」字段（string-pool idx）；编译期已知元素类型。**→ zbc 格式 bump（1.15→1.16）+ zpkg 联动（0.17→0.18）**。
- `alloc_array(element_type, elems)`；GC trace 走 `.elems`；`GcRef<Vec<Value>>` 类型签名（StrongArray/WeakArray/region_array）→ `GcRef<ArrayObj>`。

**反射表层（统一 `Type`）**：
- `make_type_from_name` 认 `[]` 后缀 → array-flavored `Type`（`name="Array"`，元素记入 `TypeDesc` 的 array-element）。
- `arr.GetType()`（object.rs）读数组 `element_type` → 元素已知的 array `Type`。
- `typeof(T[])`（VisitTypeof）emit `<elemFqn>[]` 替 `"Std.Array"`。
- `Std.Type` 加 `IsArray` + `GetElementType()`（统一 Type，与 `GetGenericArguments()` 并列）。

**覆盖（全部不擦除）**：
| 来源 | IsArray | GetElementType() |
|------|---------|------------------|
| `typeof(int[])` | true | `typeof(int)` |
| 字段/参数 `int[]` | true | `typeof(int)` |
| `arr.GetType()`（运行期值，**现携带元素**） | true | `typeof(int)` ✓ |
| `int[][]` | true | `typeof(int[])`（其元素再 `int`） |
| 非数组 | false | null |

## Scope（允许改动的文件）

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | `ArrayObj` 结构 + `Deref/DerefMut/Index/IndexMut`；`alloc_array` 收 element_type；GC trace；`StrongArray/WeakArray` payload |
| `src/runtime/src/gc/refs.rs` | MODIFY | `GcRef<Vec<Value>>` → `GcRef<ArrayObj>`（数组句柄类型） |
| `src/runtime/src/gc/region.rs` | MODIFY（如需）| region_array 元素类型 |
| `src/runtime/src/interp/exec_array.rs` | MODIFY | `ArrayNew`/`ArrayNewLit` 取元素类型名 → alloc_array |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `ArrayNew`/`ArrayNewLit` 加 `elem_type_idx`（serde default 兼容读） |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR`=16 / `ZPKG_VERSION_MINOR`=18；读 ArrayNew 新字段；version-pin 测试 |
| `src/runtime/src/metadata/types.rs` | MODIFY | `TypeDesc` array-element 表达（复用/扩 type_args） |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `make_type_from_name` 认 `[]`；`build_type` 写 element；`__type_element` builtin |
| `src/runtime/src/corelib/object.rs` | MODIFY | `arr.GetType()` 读数组 element_type |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `__type_element` |
| `src/runtime/src/jit/helpers/object.rs` ‖ array helpers | MODIFY | JIT 数组 alloc/access 适配 `ArrayObj` |
| `src/compiler/z42.IR/IrModule.cs` + `Instructions` | MODIFY | ArrayNew/Lit IR 加元素类型名 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor`=16；ArrayNew/Lit 写 elem_type_idx |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 读 ArrayNew/Lit elem_type_idx |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor`=18 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter*.cs` | MODIFY | emit ArrayNew/Lit 元素类型名（编译期数组元素类型已知）；VisitTypeof `<elem>[]` |
| `src/libraries/z42.core/src/Type.z42` | MODIFY | `IsArray` + `GetElementType()` |
| `src/z42c/z42c.ir/src/BinaryFormat/*` | MODIFY | z42c 自举 writer 同步（ZbcFormat 版本 + ArrayNew/Lit 镜像 + zbc_tests golden） |
| `docs/design/runtime/zbc.md` + `zpkg.md` + `ir.md` | MODIFY | Minor changelog + ArrayNew 字段 |
| `docs/design/language/reflection.md` | MODIFY | API 表 + Deferred 落地 |
| `src/tests/zbc-format` + `zpkg-format` fixtures | RUN | regen |
| `src/tests/types/array_element_type.z42` | NEW | golden e2e |

## Out of Scope

- 多维矩形数组 `int[,]`（z42 仅交错 `int[][]`，自然覆盖）。
- `Type.MakeArrayType()` / `MakeGenericType()` 构造侧。
- 泛型实例反射增强（`IsGenericType` 等）——本 change 只补数组，但底层统一 type_args 机制为其铺路。

## Open Questions

- [ ] `TypeDesc` array-element：复用 `type_args[0]` 还是新增专用 `array_element: Option<String>`？（design Decision 3——倾向专用字段，语义清晰，不与泛型 type_args 混淆）
- [ ] JIT 数组路径适配 `ArrayObj` 的工作量（interp-first，JIT 同步）——实施期插桩确认 JIT array helper 触点。
