# Design: 数组元素类型反射（运行期不擦除）

## Architecture

数组在创建处（`ArrayNew`/`ArrayNewLit`）由编译器写入元素类型名，运行期数组堆对象携带它，反射读出 → 全程不擦除。与 `ScriptObject.type_args`（泛型实例已不擦除）同构。

```
编译期: new int[3] / [1,2,3] / 字段·参数 int[]  —— 元素类型 = int（已知）
   │  ArrayNew/ArrayNewLit 写 elem_type_idx（string-pool "int"）  ← zbc 1.16
   ▼
运行期数组值: ArrayObj { element_type: Arc<str>="int", elems: Vec<Value> }
   │  （Deref→elems：Get/Set/Len/iter 多数消费点不变）
   ▼
arr.GetType()  → 读 element_type → array Type（IsArray, 元素=int）
typeof(int[])  → emit "int[]" → make_type_from_name 认 [] → 同款 array Type
   │
   ▼
Std.Type.IsArray / GetElementType()  （统一 Type + 能力查询，与 GetGenericArguments 并列）
```

## Decisions

### Decision 1: 数组堆 payload 加 element_type，用 Deref 收窄 blast radius
**问题**：数组值如何在运行期携带元素类型，又不重写全部 ~128 个数组消费点？
**决定**：`region_array` payload `Vec<Value>` → `ArrayObj { element_type: Arc<str>, elems: Vec<Value> }`，并为 `ArrayObj` 实现 `Deref<Target=Vec<Value>>` + `DerefMut` + `Index/IndexMut<usize>`（委托 `.elems`）。`rc.borrow().len()` / `[i]` / `iter()` / `borrow_mut().push()` 经 deref/index 透明工作。只显式改：结构定义 + trait impl + 类型签名（`GcRef<Vec<Value>>`→`GcRef<ArrayObj>`，集中在 refs.rs 的 StrongArray/WeakArray + region_array）+ `alloc_array` + `ArrayNew/Lit` exec + GC trace（trace `.elems`）+ `arr.GetType()`。
**代价**：核心 GC 结构改动 + JIT 数组 helper 同步；接受（User 授权改 VM）。

### Decision 2: 元素类型名经 ArrayNew/ArrayNewLit 字段传递 → 格式 bump
**问题**：元素类型怎么从编译期到运行期数组对象？
**决定**：`ArrayNew`/`ArrayNewLit` 各加 `elem_type_idx: u32`（string-pool idx，元素类型 FQ 名）。编译器在 codegen 数组分配处已知元素类型（`Z42ArrayType.Element` / 字面量元素静态类型）→ emit。`#[serde(default)]` 让旧 zbc 读为 0（但 strict-pin 下旧产物本就不可读，default 仅防 panic）。zbc 1.15→1.16，zpkg 0.17→0.18（联动）。**走完整 version-bumping checklist + z42c 自举 writer 同步。**

### Decision 3: TypeDesc 用专用 `array_element` 表达数组类型（不复用 type_args）
**问题**：array Type 的元素类型记哪？
**选项**：A 复用 `type_args[0]`；B 新增 `TypeDesc.array_element: Option<String>`。
**决定**：**B**。语义清晰——`type_args` 是泛型实参（`List<int>` 的 `int`），`array_element` 是数组元素（`int[]` 的 `int`），二者 C# 也分（`GetGenericArguments` vs `GetElementType`）。混用会让 `int[]` 误报 `IsGenericType`。`array_element` 是 hot `TypeDesc` 上一个 `Option<String>`（None=非数组），运行期合成 array Type 时填。

### Decision 4: 公开 API 统一 Type（不引入 ArrayType 子类）
`Std.Type` 加 `public bool IsArray;`（VM 写槽）+ `GetElementType()`（extern `__type_element`，读 `array_element` → `make_type_from_name` 或 null）。**与现有 `GetGenericArguments()` 同在统一 `Type`**——不解封 Type、不让 VM 按种类构造子类、不改 `GetType` 返回契约。「类型化建模」落在 runtime TypeDesc 的 `array_element` flavor 上（真带元素、非裸 bool）。

## Implementation Notes

- **实施顺序（interp-first）**：① 格式 + bytecode + reader（ArrayNew elem_type_idx，serde-default 先让现有 zbc 读过）→ ② ArrayObj + Deref + alloc + GC trace + exec_array（interp 数组先全绿）→ ③ JIT 数组 helper 同步 → ④ reflection.rs + Type.z42 + typeof → ⑤ z42c writer 同步 + fixtures regen + stdlib regen。每步 cargo build 卡点。
- **build_type 扩**：加 `array_element: Option<&str>`，写 `IsArray` 槽 + 经 `array_element` 暴露给 `__type_element`。Type 实例的 array_element 来自其 TypeDesc（合成）。
- **arr.GetType() 元素名**：从 `ArrayObj.element_type` 取；object.rs 构造 array Type 时把元素塞进合成 TypeDesc 的 `array_element`。
- **空数组/默认**：`new int[0]` 仍带 element_type="int"（ArrayNew 字段与 size 无关）。

## Testing Strategy

- golden `array_element_type.z42`：typeof / 字段 / 参数 / **arr.GetType()（运行期值，关键不擦除点）** / int[][] / 非数组 / 空数组。
- 回归：array_get_type / object_get_type（Name/FullName 不变）；所有数组操作 e2e（Get/Set/Len/iterate/嵌套）；zbc/zpkg fixture byte（regen）。
- GREEN：dotnet GoldenTests + cargo --lib + cargo test（含 host/JIT）+ zbc/zpkg invariant + z42c zbc 单测。
