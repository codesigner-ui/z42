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
- **THEN** 运行期抛 `InvalidCastException`

#### Scenario: null 拆箱抛异常
- **WHEN** `object o = null; int x = (int)o;`
- **THEN** 运行期抛 `InvalidCastException`

### Requirement: 重载决议装箱最低优先级

存在更具体的非装箱候选时不选装箱重载。优先级：精确匹配 > 数值加宽 > 装箱。

#### Scenario: 精确优于装箱
- **WHEN** 重载 `f(int)` 与 `f(object)` 并存，调用 `f(5)`
- **THEN** 选 `f(int)`（装箱候选仅在无非装箱候选时入选）

#### Scenario: 仅 object 重载时装箱入选
- **WHEN** 仅 `f(object)` 存在，调用 `f(5)`
- **THEN** 选 `f(object)`（装箱）

## MODIFIED Requirements

### Requirement: object 赋值兼容性

**Before:** 原始类型不可赋给 `object`（或仅显式 cast）；`object o = 5;` 报类型错误。
**After:** 原始类型经装箱转换隐式可赋给 `object`。引用类型（class/record/array）赋给 object
保持既有引用上转语义不变（它们已是 `Value::Object/Array` 带 TypeDesc，装箱=恒等，本就成立）。

## IR Mapping

| 转换 | IR | 说明 |
|------|----|------|
| 装箱 prim→object | **无新指令**（codegen elide，如恒等转换）| `Value` 已带 tag，object 槽直接持有原始 `Value`；寄存器原值流过 |
| 拆箱 object→prim | 现有 `Convert`（Object 源）| 运行期按动态 Value 变体解析；tag 不符/null → `InvalidCastException` |

无新 opcode、无 zbc/zpkg 格式 bump。

## Pipeline Steps

- [ ] Lexer：无（复用现有赋值/cast 语法）
- [ ] Parser / AST：无
- [ ] TypeChecker：装箱可赋值规则 + object→prim 拆箱合法性 + 重载决议装箱最低优先级
- [ ] IR Codegen：装箱 no-op；拆箱 emit 现有 `Convert`
- [ ] VM interp：`Convert` 的 Object→全部原始目标 + null/tag 不符抛 `InvalidCastException`（核实/补缺）

## 边界（非健全性问题，已知且对齐 deferred）

- **enum**（I64 表示）：装箱后 GetType 返 Int32、`(MyEnum)o` 与 `(int)o` 不可区分 —— 与
  enum-as-type-entity 的 deferred 对齐，不在本变更解决（届时给 enum 局部带-tag 装箱）。
- **struct/record**：已是 `Value::Object(GcRef)` 带 TypeDesc → 装箱=恒等，精确，无新增。
