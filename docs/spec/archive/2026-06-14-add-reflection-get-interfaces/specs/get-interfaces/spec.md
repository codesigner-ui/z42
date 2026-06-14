# Spec: Type.GetInterfaces()

## ADDED Requirements

### Requirement: 类直接声明的接口可反射

#### Scenario: 单接口
- **WHEN** 类 `C : IFoo` 调 `typeof(C).GetInterfaces()`
- **THEN** 返回长度 1 的 `Type[]`，唯一元素 `Name == "IFoo"`

#### Scenario: 多接口
- **WHEN** 类 `C : IFoo, IBar` 调 `typeof(C).GetInterfaces()`
- **THEN** 返回长度 2 的 `Type[]`，含 `IFoo` 与 `IBar`（按声明序）

#### Scenario: 无接口
- **WHEN** 类 `C`（仅继承隐式 Object，无接口）调 `typeof(C).GetInterfaces()`
- **THEN** 返回长度 0 的空 `Type[]`（非 null）

### Requirement: 继承链上的接口被聚合

#### Scenario: 基类接口被纳入
- **WHEN** `Base : IFoo`、`Derived : Base, IBar`，调 `typeof(Derived).GetInterfaces()`
- **THEN** 返回含 `IFoo`（来自 Base）与 `IBar`（来自 Derived），按名 dedup（重复实现同接口只出现一次）

### Requirement: 接收者一致性

#### Scenario: obj.GetType().GetInterfaces() 与 typeof 一致
- **WHEN** `C c = new C()`（`C : IFoo`），对比 `c.GetType().GetInterfaces()` 与 `typeof(C).GetInterfaces()`
- **THEN** 两者返回相同接口名集合

### Requirement: 格式 strict-pin

#### Scenario: pre-1.17 zbc 不可读
- **WHEN** 旧 zbc（minor < 17）被 1.17 reader 加载
- **THEN** strict-pin 报错（不提供兼容路径）；regen 后正常

## MODIFIED Requirements

无（纯新增 API + 新 wire 字段，不改既有行为）。

## IR Mapping

zbc TYPE section 每个类记录尾部（静态字段块之后）追加：

```
interface_count : u16
interface_name_idx[interface_count] : u32   // 接口 FQ 名，STRS pool idx
```

zpkg outer 无新字段，纯 minor 联动（zbc 强耦合规则）。

## Pipeline Steps

- [ ] Lexer — 无
- [ ] Parser / AST — 无（接口声明语法已存在）
- [ ] TypeChecker — 无（IrClassDesc.Interfaces 已由现有 codegen 填充）
- [ ] IR Codegen — 无（接口名已在 IrClassDesc.Interfaces）
- [x] zbc Writer (C#) — BuildTypeSection 写接口块 + intern
- [x] zbc Reader (C# round-trip) — ReadTypeSection 读接口块
- [x] zbc Reader (Rust) — read_type 读接口块 → ClassDesc.interfaces
- [x] TypeDesc 载入 — TypeDescCold.interfaces
- [x] VM reflection builtin — `__type_interfaces`（base-walk + dedup）
- [x] stdlib — Type.GetInterfaces()
