# Spec: Array Base Class (Std.Array as runtime base of T[])

## ADDED Requirements

### Requirement: Std.Array exists as a stdlib class

#### Scenario: Std.Array can be referenced by name in user code
- **WHEN** 用户在 z42 源码写 `Array a = someArr;` 或 `var t = typeof(Array);`
- **THEN** 编译期解析 `Array` 为 `Std.Array` 类，无 "unknown type" 错误

#### Scenario: Std.Array is sealed
- **WHEN** 用户写 `class Foo : Array { }`
- **THEN** 编译期报错 `cannot derive from sealed class 'Std.Array'`

---

### Requirement: T[] is-a Std.Array (real subtype, not catch-all)

#### Scenario: Array variable accepts arbitrary T[]
- **WHEN** `int[] xs = new int[3]; Array a = xs;`
- **THEN** 编译通过；运行时 `a` 与 `xs` 引用同一数组对象（reference identity 保持）

#### Scenario: Array variable accepts string[] / Object[] / nested arrays
- **WHEN** `Array a1 = new string[2]; Array a2 = new Object[2]; Array a3 = new int[1][];`
- **THEN** 全部编译通过（is-a 对所有元素类型成立）

#### Scenario: T[] is-a Object via Array
- **WHEN** `int[] xs = new int[3]; Object o = xs;`
- **THEN** 编译通过；通过 `Std.Array → Std.Object` 链路（不再依赖 Z42Type.cs:96 catch-all 对数组的兜底）

#### Scenario: Negative — T[] cannot be assigned to incompatible class
- **WHEN** `int[] xs = new int[3]; String s = xs;`
- **THEN** 编译报错 type mismatch（is-a 仅对 Array / Object 链路成立，不污染其他类型）

#### Scenario: arr is Array / arr is Object
- **WHEN** `int[] xs = new int[3]; bool a = xs is Array; bool b = xs is Object;`
- **THEN** `a == true` 且 `b == true`（runtime is-check 走真子类型链）

---

### Requirement: arr.GetType() returns a Std.Type instance

#### Scenario: Array of primitive returns Type with __name == "Array"
- **WHEN** `int[] xs = new int[3]; Type t = xs.GetType();`
- **THEN** `t.__name == "Array"` 且 `t.__fullName == "Std.Array"`

#### Scenario: Array of class type returns same Type info (v1: no element type)
- **WHEN** `Foo[] fs = new Foo[2]; Type t = fs.GetType();`
- **THEN** `t.__name == "Array"`（v1 不区分元素类型；元素类型属性留 expand-type-metadata）

#### Scenario: Empty array returns Type instance, not null
- **WHEN** `int[] xs = new int[0]; Type t = xs.GetType();`
- **THEN** `t.__name == "Array"`（空数组与非空数组语义一致）

#### Scenario: Negative — null array reference still bails consistently
- **WHEN** 通过 `int[]? xs = null;` 后强解引用调用 `xs!.GetType()`（或等价路径）
- **THEN** VM 抛 `NullReferenceException` —— 与对 class 实例的 null GetType() 行为一致

---

### Requirement: arr.Length goes through IR fast path (not virtual dispatch)

#### Scenario: arr.Length compiles to array_len IR instr
- **WHEN** `int[] xs = new int[5]; int n = xs.Length;`
- **THEN** IR codegen emit `array_len`（不是 `call_virtual` / `call` 到 `Std.Array.get_Length`）

#### Scenario: Length type is int
- **WHEN** `int[] xs = new int[3]; var n = xs.Length;`
- **THEN** `n` 推断为 `int`（与现有行为一致；不因 Std.Array 引入而改变）

#### Scenario: Length accessed through Array static type also fast path
- **WHEN** `int[] xs = new int[5]; Array a = xs; int n = a.Length;`
- **THEN** 仍 emit `array_len`（编译器识别底层 runtime 必为数组，可走快路径；若实现成本过高则降级为虚分发亦可，需 design.md 决策）

---

### Requirement: arr.Clone() returns a shallow copy

#### Scenario: Primitive array clone is independent
- **WHEN** `int[] xs = new int[]{1,2,3}; int[] ys = (int[])xs.Clone(); ys[0] = 99;`
- **THEN** `xs[0] == 1` 且 `ys[0] == 99`

#### Scenario: Reference array clone shares element references
- **WHEN** `Foo[] xs = new Foo[]{ new Foo() }; Foo[] ys = (Foo[])xs.Clone();`
- **THEN** `xs[0]` 与 `ys[0]` 引用相等（`Object.ReferenceEquals(xs[0], ys[0]) == true`）；但 `Object.ReferenceEquals(xs, ys) == false`

#### Scenario: Empty array clone returns empty array
- **WHEN** `int[] xs = new int[0]; int[] ys = (int[])xs.Clone();`
- **THEN** `ys.Length == 0` 且 `Object.ReferenceEquals(xs, ys) == false`

#### Scenario: Clone return type is Object (matches C# Array.Clone)
- **WHEN** `int[] xs = new int[3]; var c = xs.Clone();`
- **THEN** `c` 静态类型为 `Object`（用户必须 `(int[])xs.Clone()` 才能下行回 `int[]`，与 C# `System.Array.Clone()` 一致）

---

### Requirement: Object virtual methods (Equals/ToString/GetHashCode) callable on T[]

#### Scenario: Equals defaults to reference equality on arrays
- **WHEN** `int[] xs = new int[3]; bool eq1 = xs.Equals(xs); bool eq2 = xs.Equals(new int[3]);`
- **THEN** `eq1 == true`（自身）；`eq2 == false`（不同对象，默认 reference equality）

#### Scenario: ToString returns class name
- **WHEN** `int[] xs = new int[3]; string s = xs.ToString();`
- **THEN** `s == "Array"`（默认 `__obj_to_str` 返回 simple name；元素类型化的 ToString 留后续 spec）

#### Scenario: GetHashCode is identity-based and stable per object
- **WHEN** `int[] xs = new int[3]; int h1 = xs.GetHashCode(); int h2 = xs.GetHashCode();`
- **THEN** `h1 == h2`（同一对象 hash 稳定）

---

## MODIFIED Requirements

### Requirement: T[] → Object 赋值兼容性

**Before**: `Z42Type.cs:96` 通过 `target == Object && source is not Z42VoidType` 的 catch-all 让 `T[]` 可赋给 `Object`，但 `T[]` 在类型系统中不是 `Object` 真子类（`Object` 上的虚方法对 `T[]` 实质未挂载）。

**After**: `T[]` 通过 `Std.Array → Std.Object` 链路真子类型化；catch-all 对 `T[]` 的兜底**移除**（保留对 primitive 的 `→ Object` 兜底，因为 primitive auto-box 仍走该路径，不在本 spec 范围内）。

**可观测差异**：用户代码无差异；内部一致性提升（`IsAssignableTo(Object, T[])` 成立的原因从"catch-all"变成"真 is-a 链"）。`xs is Object` / `xs.GetType()` / `xs.Equals(...)` 等 Object 协议成员在 T[] 上首次有定义良好的语义。

---

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [ ] Lexer — 不动
- [ ] Parser / AST — 不动（`Array` 作为类型名走现有 type expr）
- [x] TypeChecker — `Z42Type.cs` IsAssignableTo 加 Array 链路；`TypeChecker.Exprs.Members.cs` `arr.Clone()` 解析；`SymbolTable` `Array` lookup
- [x] IR Codegen — `arr.Length` 仍走 `array_len`（不变）；`arr.Clone()` emit `call __array_clone`
- [x] VM interp — `builtin_obj_get_type` 加 `Value::Array` 分支；`__array_clone` builtin 实现
- [ ] JIT/AOT — 不在本范围（M4 interp 全绿前不动）

## IR Mapping

- `arr.Length` → 现有 `array_len` 指令（**不变**）
- `arr.Clone()` → `call __array_clone, [arr] → result`（走现有 builtin call 通道，无新 IR opcode）
- `arr.GetType()` → 现有 `__obj_get_type` builtin（VM 端扩展 `Value::Array` 分支，IR 不变）
- `Array a = arr` / `Object o = arr` → 现有赋值 IR（TypeChecker 通过即可，无 box / unbox）
- `xs is Array` / `xs is Object` → 现有 `is_check` 指令（VM 端识别 array 与 Std.Array / Std.Object 的 is-a 关系）
