# Spec: Class Arity Overloading

## ADDED Requirements

### Requirement: Same-name generic and non-generic classes coexist

#### Scenario: Both declared in same CU
- **WHEN** 同一 CU 声明 `class Foo { ... }` AND `class Foo<T> { ... }`
- **THEN** 编译通过 — 两者各自被注册为独立类型

#### Scenario: User code disambiguates by arity at use site
- **WHEN** 用户写 `var a = new Foo();` 和 `var b = new Foo<int>(...)`
- **THEN** 前者解析为非泛型 `Foo`，后者解析为 `Foo<int>` 实例化的泛型 `Foo<T>`

#### Scenario: NamedType resolves to non-generic only
- **WHEN** 类型表达式写 `Foo` 且同时存在 `class Foo` + `class Foo<T>`
- **THEN** 解析为非泛型 `Foo`（NamedType 路径不读取 generic 槽位）

#### Scenario: GenericType resolves to matching arity
- **WHEN** 类型表达式 `Foo<int>` 且 `class Foo<T>` 存在
- **THEN** 解析为 `Foo<T>` 的 instantiation，arity=1

### Requirement: IR name carries arity for generic classes

#### Scenario: Non-generic class IR name is bare
- **WHEN** `class Bar { ... }` codegen
- **THEN** IR class declaration emit name=`"Bar"`；ObjNew / FieldGet / VCall 引用同样为 `"Bar"`

#### Scenario: Generic class IR name has $N suffix
- **WHEN** `class Box<T> { ... }` codegen
- **THEN** IR class declaration emit name=`"Box$1"`；用户写 `new Box<int>(...)` 也 emit `"Box$1"` 作为 ObjNew 的接收类名

#### Scenario: Multi-arity generic class
- **WHEN** `class Pair<A, B> { ... }`
- **THEN** IR name 为 `"Pair$2"`

### Requirement: stdlib + cross-zpkg consistent IrName

#### Scenario: Stdlib generic class regen with IrName
- **WHEN** `regen-golden-tests.sh` 后跑 `test-vm.sh`
- **THEN** stdlib `List$1` / `Dictionary$2` / `MulticastAction$1` 等 zpkg 内 IR 用 mangled name；VM type_registry / lazy_loader / cross-zpkg lookup 全部一致

#### Scenario: Cross-zpkg generic class import
- **WHEN** 用户 zpkg 引用 stdlib `List<int>`
- **THEN** IR ObjNew receiver=`"List$1"`，VM lookup `type_registry["List$1"]` 命中

### Requirement: Diagnostics + typeof preserve user-facing name

#### Scenario: Error message uses bare Name
- **WHEN** TypeChecker 报 `cannot instantiate abstract class \`Foo\`` 或 `class \`Foo<T>\` ...`
- **THEN** 用户看到的诊断仍是源码 `Foo` / `Foo<T>` 形态，不是 mangled `Foo$1`（IrName 仅是内部 key）

#### Scenario: typeof returns user-facing name
- **WHEN** `typeof(Foo<int>)` 求值
- **THEN** 字符串结果为 `"Foo"` 或 `"Foo<int>"`（与 typeof 现有语义一致），**不是** `"Foo$1"`

## MODIFIED Requirements

### Requirement: Class registry key

**Before**: `_classes: Dictionary<string, Z42ClassType>` keyed by **bare** `cls.Name` 永远取 simple name；同名 class 一律冲突 → E0408 / E0411

**After**: `_classes` keyed by **`IrName`**：
- 非泛型类 IrName = `Name`
- 泛型类 IrName = `$"{Name}${TypeParams.Count}"`

允许同一 simple name 下 arity-disjoint 的两类共存（`Foo` + `Foo$1` + `Foo$2` ...）。同名 + 同 arity 仍是 E0408 duplicate。

### Requirement: ResolveType for class names

**Before**:
```
NamedType("Foo")     → _classes["Foo"]
GenericType("Foo", args) → _classes["Foo"]  ← 与 NamedType 同 key
```

**After**:
```
NamedType("Foo")     → _classes["Foo"]                (arity 0 槽位)
GenericType("Foo", args) → _classes["Foo${args.Count}"]  (arity N 槽位)
```

向后兼容：原"只有泛型 List<T>"的场景下，**注册后**生成 `_classes["List$1"]`；
源码 `List<int>` 走 GenericType 路径，匹配 `List$1` ✓；如有人裸写 `List`（无 type-args），
NamedType 路径在 `_classes["List"]` 找不到 → 类型错误（符合预期：泛型类不能被裸名引用）。

### Requirement: IR class FQ name emission

**Before**: IrGen emit `cls.Name` 作 IR 类名；VM 由该字符串作 type_registry key

**After**: IrGen emit `cls.IrName`（生成或派生：non-generic 同 Name；generic 加 `$N`）；VM 透明使用此字符串作 type_registry key 与跨 zpkg 名查找

zbc 二进制无格式 bump：name 字段语义从"短名"扩为"短名 + 可选 arity 后缀"，serializer 视为字符串透明。

## Pipeline Steps

- [ ] Lexer — 无变化
- [ ] Parser / AST — 无变化
- [ ] TypeChecker — registry key + ResolveType GenericType 分支
- [ ] IR Codegen — 类 IR 名 + 类引用全部走 IrName
- [ ] VM interp — 透明（既有 type_registry 字符串 lookup 已经按字符串）
- [ ] VM JIT — 透明
- [ ] zbc / TSIG round-trip — 透明（字符串 round-trip）
