# Proposal: Std.Array 作为 T[] 的运行时基类（v1 骨架）

## Why

z42 当前 `T[]` 由 `Z42ArrayType` 表示，与 `Z42ClassType` 平级独立，**没有 stdlib 类型表示，也不在 Object 继承链上**。具体后果：

1. **`arr.GetType()` 没法返回有意义的结果** —— `src/runtime/src/corelib/object.rs:11-16` 的 `builtin_obj_get_type` 只接受 `Value::Object`，传入 `Value::Array` 会 `bail!("expected an object")`。这是当前 z42 的真实空洞，未来反射、诊断、`obj.GetType().__name` 等通用代码必须先解决
2. **没有可挂载虚方法/协议槽位的"类型实体"** —— Sort/IndexOf/Copy/Fill/Reverse 等静态算法、`Clone()` / `CopyTo()` 实例方法、`IEnumerable<T>` 接入、协变（`Dog[]` → `Animal[]`）等未来工作都没有归属类型，必须先确立 `Std.Array` 作为载体
3. **`T[]` → `Object` 的"赋值兼容"靠 `Z42Type.cs:96` 的 catch-all `target == Object` 兜底**，不是真 is-a；`Object` 上声明的 `GetType()` / `Equals()` / `ToString()` / `GetHashCode()` 当前对 `T[]` 全无定义。这种"假兼容"属于设计偏移，沿用越久越难纠正

C# `System.Array` 把所有 `T[]` 接入 `System.Object` 子树，让 `T[]` 享有反射、协变、虚方法分发等全套能力。z42 选择走同款模型（**1A 决策**，[memory: feedback_stdlib_docs_not_final.md]），本变更是该方向的**最小骨架步**。

## What Changes

**v1 仅做骨架（M0）**：
- 新增 stdlib 类 `Std.Array`，作为所有 `T[]` 的运行时基类
- TypeChecker：`T[]` 真正 is-a `Std.Array`（替换 `Z42Type.cs:96` 对 `T[]` → `Object` 的 catch-all 路径，改为 `T[]` → `Std.Array` → `Std.Object` 的真正子类型链）
- VM：`__obj_get_type(arr)` 不再 bail，返回 `Type { __name="Array", __fullName="Std.Array" }`
- `Std.Array` v1 仅暴露最小成员：
  - `int Length { get; }` — 桥接到 IR `array_len`，**保留快路径**（不退化为虚分发）
  - `Object Clone()` — 浅拷贝，新 builtin `__array_clone`
- `Std.Array` 标 `sealed`（用户不能 `class Foo : Array`，`T[]` 通过编译器特殊处理 is-a）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/Array.z42` | NEW | `Std.Array` 类定义（sealed class，Length property + Clone() 方法 + Native 注解） |
| `src/libraries/z42.core/README.md` | MODIFY | 核心文件表加一行 `Array.z42` |
| `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` | MODIFY | `IsAssignableTo`：`T[]` 视为 `Std.Array` 子类型（取代 line 96 catch-all 中针对数组的部分）；保持其他兼容规则不变 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Members.cs` | MODIFY | `arr.Length` / `arr.Clone()` 成员解析：`Length` 走原 IR 快路径（identity 不变），`Clone` 解析到 `Std.Array.Clone` |
| `src/runtime/src/corelib/object.rs` | MODIFY | `builtin_obj_get_type` 加 `Value::Array` 分支：返回 `Std.Type` 对象（v1 不带元素类型） |
| `src/runtime/src/corelib/array.rs` | NEW | `__array_clone` builtin 实现（浅拷贝 `Vec<Value>`） |
| `src/runtime/src/corelib/array_tests.rs` | NEW | `__array_clone` / `__obj_get_type` for array 单元测试 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 引入 `pub mod array;` + 把 `__array_clone` 注册到 dispatch table |
| `src/runtime/src/metadata/well_known_names.rs` | MODIFY | 新增 `pub const STD_ARRAY: &str = "Std.Array";` |
| `src/compiler/z42.IR/WellKnownNames.cs` | MODIFY | 新增 `public const string StdArray = "Std.Array";`（与 Rust 侧对齐） |
| `docs/design/arrays.md` | MODIFY | 新增 "运行时基类 Std.Array" 段（v1 范围 + 后续 spec 引用） |
| `docs/design/object-protocol.md` | MODIFY | `GetType()` 对 `T[]` 的语义说明 |
| `src/compiler/z42.Tests/Semantics/ArrayBaseClassTests.cs` | NEW | `T[]` is-a `Array` is-a `Object`、`arr.Length` / `arr.Clone()` 类型解析、负面测试 |
| `src/runtime/tests/golden/run/array_get_type/source.z42` | NEW | golden：`arr.GetType().__name == "Array"` |
| `src/runtime/tests/golden/run/array_get_type/expected.txt` | NEW | golden 期望输出 |
| `src/runtime/tests/golden/run/array_clone/source.z42` | NEW | golden：浅拷贝原数组改一个不影响另一个 |
| `src/runtime/tests/golden/run/array_clone/expected.txt` | NEW | golden 期望输出 |

**只读引用**（理解上下文必须读，但不修改）：

- `src/libraries/z42.core/src/Object.z42` — Object 上 `GetType()` / `Equals()` / `ToString()` / `GetHashCode()` 的虚方法签名
- `src/libraries/z42.core/src/Type.z42` — `Std.Type` 字段约定（`__name` / `__fullName`）
- `src/runtime/src/interp/exec_instr.rs` — 现有 `array_len` / `array_get` / `array_set` / `array_new` 指令实现
- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` — IR 数组指令的 emit 路径

## Out of Scope

明确**不**在本变更范围内（各自独立 spec）：

- 静态算法（`Sort` / `IndexOf` / `Copy` / `Fill` / `Reverse` / `BinarySearch`）→ 后续 `add-array-static-algos`
- `Std.Array` 实现 `IEnumerable<T>`（让 `arr` 能传给 LINQ / List 构造器等）→ 后续 `add-array-ienumerable`
- 协变（`Dog[]` 赋给 `Animal[]`）→ 后续 `add-array-covariance`（L3 候选）
- `Type.ElementType` 反射元数据（让 `arr.GetType().__name == "int[]"` 或拥有元素 Type 字段）→ 后续 `expand-type-metadata`
- 修改 IR 数组指令（`array_new` / `array_get` / `array_set` / `array_len` 维持原状）
- VM `Value::Array` 内部表示改动（仍是 `Rc<RefCell<Vec<Value>>>`）
- `arr.Length` 退化为虚分发（保留 IR 快路径）
- 多维数组 `T[,]` / jagged `T[][]` 的基类讨论

## Open Questions

User 已在 2026-05-04 探索阶段拍板（按当时 1–5 建议）：

1. ✅ `Std.Array` 标 sealed — 用户不能继承，`T[]` 通过编译器特殊处理 is-a
2. ✅ `Length` 类型为 `int`（与 IR `array_len` 返回 i32 一致）
3. ✅ `Clone()` 是浅拷贝 — 元素是引用类型则共享引用，与 C# `Array.Clone()` 语义一致
4. ✅ `arr.GetType().__name` 返回 `"Array"`（不带元素类型）；元素类型留给后续 `expand-type-metadata`
5. ✅ `arr.Length` 保留 IR `array_len` 快路径，不退化为虚调用

无遗留 open question；进入 spec/design/tasks 阶段。
