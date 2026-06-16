# Spec: IsAssignableFrom / GetInterface + FQ 接口身份

## ADDED Requirements

### Requirement: IsAssignableFrom(Type)

#### Scenario: 自反
- **WHEN** `typeof(C).IsAssignableFrom(typeof(C))`
- **THEN** true

#### Scenario: 基类 ← 派生类
- **WHEN** `typeof(Base).IsAssignableFrom(typeof(Derived))`（`class Derived : Base`）
- **THEN** true（方向：接收者是更宽类型）

#### Scenario: 方向相反
- **WHEN** `typeof(Derived).IsAssignableFrom(typeof(Base))`
- **THEN** false

#### Scenario: 接口 ← 实现类
- **WHEN** `typeof(IShape).IsAssignableFrom(typeof(Circle))`（`class Circle : IShape`）
- **THEN** true（真实接口身份比较）

#### Scenario: null / 无关
- **WHEN** `typeof(C).IsAssignableFrom(null)` / `typeof(C).IsAssignableFrom(typeof(D))`（无关）
- **THEN** false

### Requirement: GetInterface(string)

#### Scenario: 命中 / 未命中
- **WHEN** `typeof(Circle).GetInterface("IShape")` / `typeof(Circle).GetInterface("INope")`
- **THEN** 返回 `IShape` 的 Type / null

### Requirement: GetInterfaces 返回真接口句柄

#### Scenario: 接口块存 FQ 名 → 真句柄
- **WHEN** `typeof(Circle).GetInterfaces()[0]`（`class Circle : IShape`）
- **THEN** 是带句柄的 Type：`.IsInterface == true`、`.FullName == "<ns>.IShape"`
  （此前接口块存 bare 名 → name-only，`IsInterface` 为 false）

## MODIFIED Requirements

### Requirement: 接口块存 FQ 名

**Before:** TYPE section 接口块存接口 **bare 名**（`"IShape"`，add-reflection-get-interfaces）。
`GetInterfaces()` 据此产 name-only Type；接口身份跨命名空间同名不可区分。

**After:** 接口块存 **FQ 名**（`"Demo.IShape"`，编译期 `QualifyClassName`）。`GetInterfaces()`
解析到真接口句柄；接口身份按 FQ 名 robust 比较。

### Requirement: `is` / `as` 对接口生效

**Before:** `is_subclass_or_eq_td`（`x is`/`as` 权威判定）只走 base_name 类链，不查接口 →
`circle is IShape` 返回 **false**。

**After:** base 链每层额外比较声明的接口（FQ 名）→ `circle is IShape` / `circle as IShape`
**对接口正确生效**。传递接口（接口继承接口）仍不覆盖（延后）。

## IR Mapping

接口块字段语义 bare→FQ（zbc 1.19→1.20 / zpkg 0.21→0.22）；结构不变（`u16 count + str idx[]`）。
`IsAssignableFrom` = native builtin `__type_is_assignable_from`（无新 IR 指令）。

## Pipeline Steps

- [x] IR Codegen — `InterfaceTypeName` → `QualifyClassName`（接口块 FQ）
- [x] zbc writer/reader — 接口名语义 + version bump
- [x] VM — `is_subclass_or_eq_td` 查接口 + `builtin_type_is_assignable_from`
- [x] stdlib — `Type.IsAssignableFrom(Type)` extern + `GetInterface(string)` z42
