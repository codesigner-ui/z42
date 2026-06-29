# 装箱与拆箱（primitive ↔ object）

> 来源：`add-boxing-conversions`（0.3.11）。Method.Invoke（0.3.12）的前置。

## 设计要点

z42 运行期 `Value` 是 **tagged union**——`I64/F64/Bool/Char/Str` 与 `Object/Array` 平级，
`object[]` 即 `Vec<Value>`。因此装箱**不是把栈值搬到堆**（那一步物理上不存在），而是一个
**类型系统层**的概念：

- **装箱（primitive → object）**：原始值赋给 `object` 目标。运行期值不变（仍是 `Value::I64` 等），
  **零堆分配、零 GC、无新 IR 指令**——如恒等转换般由 codegen elide。
- **拆箱（object → primitive）**：`(T)o` 经现有 `Convert` 指令，运行期按动态 `Value` 变体解析；
  类型不符或 null → `InvalidCastException`。

**未采用 C#/JVM 式堆包装**（`Value::Boxed`/堆 Integer + Box/Unbox IR）：z42 Value 已统一表示，
堆包装是冗余的双重装箱（每次 box 一次分配 + GC + 写屏障），性能更差且无语义收益。值类型语义
（值相等、无装箱身份）在类型系统层方案下天然正确。

## 语义规则

| 方向 | 规则 |
|------|------|
| prim → object | 隐式可赋值（任何类型可赋给 `object`）。codegen no-op。|
| object → prim | 显式 cast `(T)o`，运行期受检；不符/null 抛 `InvalidCastException`。|
| object[] 元素 | `a[i] = prim` 逐元素装箱（no-op）；`(T)a[i]` 逐元素拆箱。|
| 引用类型 → object | 引用上转（class/record/array 已是带 TypeDesc 的 GcRef，装箱=恒等）。|
| 数组协变 | **不引入** `int[] <: object[]`（避免 store-hole）；装箱仅作用于标量/逐元素赋值。|

整数拆箱用 **convert 语义**（非严格精确匹配）：z42 所有整型运行期都是 `Value::I64`，装箱 int 与
装箱 long 同表示、不可区分，故 `(int)o` 走 `Convert` 截断而非精确匹配——这是表示决定的唯一可行方式。

## GetType 与精度边界

装箱值的 `GetType()` 由 `Value` tag 映射：

| 装箱目标 | 表示 | GetType 精度 |
|---------|------|------------|
| i32/i64/f32/f64/bool/char | 各自 tag，与类型 1:1 | ✅ 精确 |
| struct / record | `Value::Object(GcRef)` 带 TypeDesc | ✅ 精确（装箱=恒等）|
| **enum** | I64（无独立 tag）| ⚠️ 丢精度 → 返底层 Int32 |

enum 不精确是**已知边界、非不健全**：拆箱仍受检（不会把 int 当 string）。精确 enum 装箱待
enum-as-type-entity（见 Deferred）。

## InvalidCastException 的可捕获性

拆箱失败经运行期 `Convert` 的内部错误产生，当前是**终止性 VM 异常、不可被 `try/catch` 捕获**——
与所有 `Convert` 失败（如 `(char)非法码点`）一致。让 `Convert` 错误成为可捕获的 z42 异常是独立的
既有问题，不属于装箱机制。

## 健全性

装箱 = 加宽上转（prim→object，永远安全）+ 受检下转（object→prim，运行期查 tag）。`Value` 始终
携带具体 tag，下转可靠校验，无法把一个类型当另一个用——与 Java `Object` + cast 同种健全性。

## Deferred / Future Work

### add-boxing-future-enum-precise
- **来源**：add-boxing-conversions 实施期
- **触发原因**：enum 当前 I64 表示，装箱丢类型精度（GetType→Int32，`(MyEnum)o` 与 `(int)o` 不可区分）
- **前置依赖**：enum-as-type-entity（IsEnum + TYPE 条目 + 独立 tag/带-tag 装箱）
- **触发条件**：enum 作独立类型实体规划时
- **当前 workaround**：装箱 enum 视作其底层 int；需精确类型时不经 object 中转
