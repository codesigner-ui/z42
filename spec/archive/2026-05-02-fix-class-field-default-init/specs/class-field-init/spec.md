# Spec: Class Field Default Initialization

## ADDED Requirements

### Requirement: Instance field declared without initializer takes type default

Every value-type instance field declared without `=` initializer must be visible as the type's default value (not `Null`) at first read after `new`.

#### Scenario: int field default
- **WHEN** `class Box { int n; }` and `var b = new Box();`
- **THEN** `b.n` reads as `0`（`I64(0)` in VM）, not `Null`

#### Scenario: bool field default
- **WHEN** `class Flag { bool on; }` and `var f = new Flag();`
- **THEN** `f.on` reads as `false`（`Bool(false)` in VM）

#### Scenario: f64 field default
- **WHEN** `class Pt { f64 x; }` and `var p = new Pt();`
- **THEN** `p.x` reads as `0.0`（`F64(0.0)` in VM）

#### Scenario: str / reference field default
- **WHEN** `class Bag { string s; int[] xs; }` and `var b = new Bag();`
- **THEN** `b.s` 与 `b.xs` 均为 `Null`（reference 类型默认 null 与 C# 一致）

### Requirement: Instance field with initializer evaluates the expression on construction

`int n = 5;` 这类带 `=` 的字段必须在 `new` 时把 init 表达式求值后写入字段；TypeCheck 必须验证 init 表达式与字段类型兼容。

#### Scenario: literal initializer
- **WHEN** `class Box { int n = 5; }` and `var b = new Box();`
- **THEN** `b.n == 5`

#### Scenario: expression initializer
- **WHEN** `class C { int n = 1 + 2; string s = "hi" + "!"; }` and `var c = new C();`
- **THEN** `c.n == 3` 且 `c.s == "hi!"`

#### Scenario: type mismatch reports diagnostic
- **WHEN** `class Bad { int n = "string"; }`
- **THEN** TypeChecker 报 `Z????` 类型不匹配错误，编译失败

#### Scenario: initializer evaluation order is field declaration order
- **WHEN** 多个有 init 的字段声明
- **THEN** init 按字段声明顺序求值（前面字段已初始化、后面字段尚未）

### Requirement: Class without explicit ctor synthesizes implicit ctor when any field has initializer

无显式 ctor 但任一实例字段有显式 init → 编译器合成无参隐式 ctor `<ClassName>.<ClassName>`，仅含 base ctor call（如有）+ 字段 init。

#### Scenario: implicit ctor synthesized for class with field init
- **WHEN** `class Box { int n = 5; }` 无显式 ctor，`var b = new Box();`
- **THEN** 编译器合成 `Box.Box`；`b.n == 5`

#### Scenario: no implicit ctor when no field has init
- **WHEN** `class Tag { int n; string s; }` 无显式 ctor，`var t = new Tag();`
- **THEN** 不合成 ctor（沿用现有"VM 跳过 ctor call"路径），字段由 P3 type defaults 提供（`t.n == 0`、`t.s == Null`）

#### Scenario: explicit ctor coexists with field init
- **WHEN** `class Box { int n = 5; Box(int x) { this.n = x; } }`，`var b = new Box(99);`
- **THEN** ctor 入口先把 `n` init 为 `5`，用户 body `this.n = x` 再覆写为 `99`，`b.n == 99`

### Requirement: Inherited base class fields follow base ctor's init logic

子类 ctor 在 base ctor call 完成后才注入子类自身字段的 init；base 类字段由 base ctor 负责。

#### Scenario: base class with field init runs base ctor first
- **WHEN** `class A { int a = 1; } class B : A { int b = 2; }` 且 `var x = new B();`
- **THEN** `x.a == 1` 且 `x.b == 2`

#### Scenario: parent without zero-arg ctor blocks implicit ctor synthesis
- **WHEN** `class Parent { Parent(int x) {} } class Child : Parent { int n = 5; }`（Child 无显式 ctor）
- **THEN** TypeChecker 报错 `Z????`（"class with field initializer must declare an explicit constructor when base class has no parameterless ctor"），编译失败

## MODIFIED Requirements

### Requirement: ObjNew slot initialization

**Before**: VM ObjNew 把所有字段 slot 一律置为 `Value::Null`，不区分声明类型；ctor 调用前/后无差异。

**After**: VM ObjNew 按 `FieldSlot.type_tag` 选默认值：

| type_tag | default Value |
|----------|---------------|
| `i32` / `i64` / `i8` / `i16` / `u8` / `u16` / `u32` / `u64` | `Value::I64(0)` |
| `f64` / `f32` | `Value::F64(0.0)` |
| `bool` | `Value::Bool(false)` |
| `char` | `Value::I64(0)`（与现有 char 表示一致） |
| `str` / class name / array type / option type | `Value::Null` |
| 未知 | `Value::Null`（fallback） |

interp 与 JIT helper 必须共享同一 `default_value_for` 函数，行为完全一致。

## IR Mapping

无新 IR 指令。复用现有：
- `FieldSetInstr` —— ctor 入口注入的字段 init 翻译为 `field_set this <name> <reg>`
- `ObjNewInstr` —— 语义不变，slot 初始化逻辑由 VM 层调整

## Pipeline Steps

- [x] Lexer — 无影响
- [x] Parser / AST — 无影响（`FieldDecl.Initializer` 已存在）
- [ ] TypeChecker — 新增 instance field init 绑定（P1）
- [ ] IR Codegen — ctor 注入 field init + 合成隐式 ctor（P2）
- [ ] VM interp — `ObjNew` 类型默认值（P3）
- [ ] VM JIT — `jit_obj_new` 镜像 P3
