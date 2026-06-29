# Spec: 装箱转换（boxing / unboxing）

## ADDED Requirements

### Requirement: 隐式装箱（primitive → object）

原始类型表达式（i32/i64/f32/f64/bool/char）可隐式转换为 `object`（装箱可赋值），
无需显式 cast。运行期值不变（无堆分配）。

#### Scenario: 装箱到 object 变量
- **WHEN** `object o = 5;`
- **THEN** 编译通过；运行期 `o` 持有 `Value::I64(5)`（无堆分配）

#### Scenario: 原始值赋入 object[] 元素
- **WHEN** `object[] a = new object[2]; a[0] = true; a[1] = 'x';`
- **THEN** 编译通过；`a[0]` = `Value::Bool(true)`、`a[1]` = `Value::Char('x')`

#### Scenario: 传原始值给 object 形参
- **WHEN** 方法 `void f(object x)`，调用 `f(3.14);`
- **THEN** 编译通过（装箱转换）；`x` 持有 `Value::F64(3.14)`

#### Scenario: 装箱后 GetType 返回原始类型
- **WHEN** `object o = 5; Type t = o.GetType();`
- **THEN** `t` == `typeof(int)`（tag→Type 映射；非堆包装也精确，因 i64 与 int 1:1）

### Requirement: 受检拆箱（object → primitive）

`(T_prim)o` 对 `object` 源做运行期 tag 校验：匹配则取值，不匹配/为 null 抛 `InvalidCastException`。
复用现有 `Convert` 指令（Object 源已运行期按动态 Value 变体解析）。

#### Scenario: 正确拆箱
- **WHEN** `object o = 5; int x = (int)o;`
- **THEN** `x == 5`

#### Scenario: 类型不符拆箱抛异常
- **WHEN** `object o = "hi"; int x = (int)o;`
- **THEN** 运行期抛 `InvalidCastException`（消息含目标 tag）
- **注（实证）**：当前经运行期 `Convert` 的 `bail!` 产生，是**终止性 VM 异常、不可被 try/catch
  捕获**——与所有 Convert 失败一致。让 Convert 错误可捕获是独立的既有问题，不在本变更 scope。

#### Scenario: null 拆箱抛异常
- **WHEN** `object o = null; int x = (int)o;`
- **THEN** 运行期抛 `InvalidCastException`（同上，不可捕获）

> **重载决议（N/A，实证移除）**：z42 重载是 **arity-only**（`_overloadKey = name+"$"+argCount`），
> **不支持同名同 arity 的类型重载**，故不存在 `f(int)` 与 `f(object)` 并存——"装箱最低优先级"
> 需求不适用，本变更不涉及重载决议。

## MODIFIED Requirements

### Requirement: object 赋值兼容性（实证：大部已存在）

**实证更正**：prim→object 隐式可赋值**已存在**（GS6，`TypeChecker.z42:1118`「任何类型可赋给
object」）；`(T)object` 拆箱 cast 已走 `BoundConvert`→`Convert`。引用类型（class/record/array）
赋 object 也本就成立（已是 `Value::Object/Array` 带 TypeDesc，装箱=恒等）。

**本变更的实际 delta** = 运行期 `Convert` 的 **Bool 拆箱恒等**（`(Value::Bool(_), T_BOOL)`）——此前
bool 无数值 arm 误落 `InvalidCastException` bail。补此一处后 prim↔object 全类型往返成立。
**z42c.semantics 零改动**（端到端实证：未动编译器即编过全部装箱/拆箱用例）。

## IR Mapping

| 转换 | IR | 说明 |
|------|----|------|
| 装箱 prim→object | **无新指令**（codegen elide，如恒等转换）| `Value` 已带 tag，object 槽直接持有原始 `Value`；寄存器原值流过 |
| 拆箱 object→prim | 现有 `Convert`（Object 源）| 运行期按动态 Value 变体解析；tag 不符/null → `InvalidCastException` |

无新 opcode、无 zbc/zpkg 格式 bump。

## Pipeline Steps（实证：仅 VM interp 一处改动）

- [x] Lexer：无（复用现有赋值/cast 语法）
- [x] Parser / AST：无
- [x] TypeChecker：**零改动**（GS6 prim→object 已可赋值；`(T)object` 已走 BoundConvert；重载 arity-only → 优先级 N/A）
- [x] IR Codegen：**零改动**（装箱已 no-op；拆箱已 emit `Convert`）
- [x] VM interp：`Convert` 补 **Bool 拆箱恒等**（唯一改动）；int/long/char/f64 拆箱本就工作；null/tag 不符已抛 `InvalidCastException`

## 边界（非健全性问题，已知且对齐 deferred）

- **enum**（I64 表示）：装箱后 GetType 返 Int32、`(MyEnum)o` 与 `(int)o` 不可区分 —— 与
  enum-as-type-entity 的 deferred 对齐，不在本变更解决（届时给 enum 局部带-tag 装箱）。
- **struct/record**：已是 `Value::Object(GcRef)` 带 TypeDesc → 装箱=恒等，精确，无新增。
