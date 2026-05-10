# Tasks: add-array-base-class (v1 骨架)

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07 | 类型：feat(stdlib + lang + vm)

## 进度概览

- [x] 阶段 1: stdlib `Std.Array` 类 + native binding 注册
- [x] 阶段 2: TypeChecker `T[]` is-a `Std.Array`（IsAssignableTo 显式分支）
- [x] 阶段 3: VM `__obj_*` builtin 加 Value::Array 分支 + `__array_clone` builtin + is_instance/as_cast (interp + JIT) for arrays + VCall 路由 `Std.Array.<method>` 走 `primitive_class_name`
- [x] 阶段 4: 测试（3 golden + 5 cargo unit）+ 文档同步
- [x] 阶段 5: 全绿验证 + 归档

## 实施备注

- `Std.Array` 加完整 Object 协议成员（GetType / Equals / ToString / GetHashCode）+ Clone，全 Native 绑定到现有 `__obj_*` / `__array_clone` builtin
- 三个 VM builtin（`__obj_hash_code` / `__obj_equals` / `__obj_to_str`）加 `Value::Array` 分支：identity hash / reference equality / "Array" 字面量
- `is_instance` / `as_cast` interp + JIT 双端硬编码 `Array | Object | Std.Array | Std.Object` 子类型
- VCall `primitive_class_name` 添加 `Value::Array → STD_ARRAY` 路由，让 `arr.Clone()` / `arr.GetType()` 等通过既有 `Std.<class>.<method>` 直查 func_index 通道（与 Std.int / Std.String 同款）
- IncrementalBuildIntegrationTests `cached: 47/47` 同步更新到 `48/48`（新加 Array.z42）

## Out of Scope（保留 follow-up）

- `(int[])arr.Clone()` 显式 cast 回 T[] —— z42 parser 当前不支持 `(T)expr` cast，独立 spec
- `Object`-typed 变量上 `obj.GetType()` resolution —— TypeChecker 路径需调研
- 静态算法（Sort/IndexOf/Copy/Fill/Reverse）
- IEnumerable<T> 接入
- 协变 `Dog[]` → `Animal[]`
- 元素类型反射元数据（`arr.GetType().__name == "int[]"`）
- 多维数组 / jagged array

## 阶段 1: Stdlib

- [ ] 1.1 [src/libraries/z42.core/src/Array.z42](../../src/libraries/z42.core/src/Array.z42) NEW — `public sealed class Array { public int Length; private Array(); [Native("__array_clone")] public extern Object Clone(); }`
- [ ] 1.2 [src/runtime/src/metadata/well_known_names.rs](../../src/runtime/src/metadata/well_known_names.rs) 加 `pub const STD_ARRAY: &str = "Std.Array";` + `BUILTIN_ARRAY_CLONE: &str = "__array_clone"`
- [ ] 1.3 [src/compiler/z42.IR/WellKnownNames.cs](../../src/compiler/z42.IR/WellKnownNames.cs) 加对应常量
- [ ] 1.4 验证：`./scripts/build-stdlib.sh` 通过

## 阶段 2: TypeChecker

- [ ] 2.1 [src/compiler/z42.Semantics/TypeCheck/Z42Type.cs](../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs) `IsAssignableTo`：source 是 Z42ArrayType 时，target 是 Z42ClassType{Name in {"Array","Object"}} → true；移除原 catch-all 对 array 的兜底
- [ ] 2.2 [src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs](../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs) `arr.Length` resolution：当 receiver 是 Z42ArrayType（仍走 FieldGet 快路径，无变更）；当 receiver 已上转为 Z42ClassType("Array") + Length 字段查询走 Std.Array.Length 字段（也 emit FieldGet）
- [ ] 2.3 同上：`arr.Clone()` 解析到 `Std.Array.Clone` 方法（receiver 是 Z42ArrayType 或 Z42ClassType("Array")）
- [ ] 2.4 同上：`arr.GetType() / Equals / ToString / GetHashCode` 通过 Std.Array 继承链解析到 Std.Object（receiver 是 Z42ArrayType 时也允许 — 对待为 Std.Array 实例）
- [ ] 2.5 验证：`dotnet build` 通过

## 阶段 3: VM

- [ ] 3.1 [src/runtime/src/corelib/array.rs](../../src/runtime/src/corelib/array.rs) NEW — `builtin_array_clone(args)` 浅拷贝 Vec<Value>
- [ ] 3.2 [src/runtime/src/corelib/array_tests.rs](../../src/runtime/src/corelib/array_tests.rs) NEW — clone primitive / ref / empty
- [ ] 3.3 [src/runtime/src/corelib/mod.rs](../../src/runtime/src/corelib/mod.rs) 注册 `pub mod array;` + dispatch `__array_clone`
- [ ] 3.4 [src/runtime/src/corelib/object.rs](../../src/runtime/src/corelib/object.rs) `builtin_obj_get_type` 加 `Value::Array` 分支：返回 Type{__name="Array", __fullName="Std.Array"}
- [ ] 3.5 VM `is_instance` / `as_cast` 指令处理 `Value::Array` 接收者：if class_name in {"Std.Array","Std.Object","Array","Object"} → true（取决于 IR 是否带 namespace 前缀；以 IR emit 现状对齐）
- [ ] 3.6 验证：`cargo build --release` 通过

## 阶段 4: 测试 + 文档

- [ ] 4.1 [src/compiler/z42.Tests/Semantics/ArrayBaseClassTests.cs](../../src/compiler/z42.Tests/Semantics/ArrayBaseClassTests.cs) NEW — IsAssignableTo / 成员解析 / sealed 否定测试
- [ ] 4.2 [src/tests/types/array_get_type/](../../src/tests/types/array_get_type/) NEW golden — `int[] xs = ...; xs.GetType().__fullName` == "Std.Array"
- [ ] 4.3 [src/tests/types/array_clone/](../../src/tests/types/array_clone/) NEW golden — 浅拷贝原数组改一个不影响另一个；元素引用共享
- [ ] 4.4 [src/tests/types/array_is_instance/](../../src/tests/types/array_is_instance/) NEW golden — `xs is Array` / `xs is Object`
- [ ] 4.5 [docs/design/language/arrays.md](../../docs/design/language/arrays.md) 加"运行时基类 Std.Array"段
- [ ] 4.6 [docs/design/language/object-protocol.md](../../docs/design/language/object-protocol.md) GetType() for T[] 语义说明
- [ ] 4.7 现有数组测试（24_arrays 等）不破

## 阶段 5: 验证 + 归档

- [ ] 5.1 dotnet build + cargo build 全绿
- [ ] 5.2 dotnet test（含新 ArrayBaseClassTests）全绿
- [ ] 5.3 ./scripts/test-vm.sh 全绿（300 + 3 个新 golden）
- [ ] 5.4 commit + push + 归档 → docs/spec/archive/2026-05-07-add-array-base-class/

## 实施风险 / 中断点

- **R1**：Std.Array `private ctor` 触发 SymbolCollector 对无 public ctor 的类报 E0xxx？需先确认；若是则改用 `internal Array()` 或保持 public ctor + 标 sealed 防止 derive
- **R2**：现有 Z42ArrayType 在 FieldGet 路径下 Length 是 i64（VM 端硬编码），但 Std.Array.Length 声明 `int`（类型期望 i32）—— 类型协商可能需要在 IrGen / VM 加 widen 路径
- **R3**：`xs is Array` 在 IR 端是 `IsInstance` opcode，`class_name` 字段（string）；VM 端需识别这个 string 是 "Std.Array"（with namespace）还是 "Array"（no ns）—— 取决于 TypeChecker 写 qualName 时是否前缀 Std；以现有 IsInstance 测试为准
- **R4**：`Std.Array` 出现在 stdlib zpkg 后，`build-type-registry` 需要把它加进去并设 BaseClassName="Std.Object"；如果继承解析失败需要修

任意一项触发"超出 1.5x 工作量"或"架构性发现" → 停下汇报，部分回滚或登记 follow-up。
