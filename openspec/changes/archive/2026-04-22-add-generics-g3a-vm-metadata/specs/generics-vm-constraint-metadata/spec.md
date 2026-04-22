# Spec: VM 约束元数据 + 加载时校验

## ADDED Requirements

### Requirement: zbc 二进制携带约束元数据

泛型函数和泛型类的 type parameter 在 zbc 的 SIGS / TYPE section 中必须携带完整约束信息。

#### Scenario: 带接口约束的泛型函数
- **WHEN** 编译 `T Max<T>(T a, T b) where T: IComparable<T> { ... }`
- **THEN** zbc 的 SIGS section 中该函数的 type_param `T` 有 `interface_count=1`，interface_name_idx 指向 `IComparable`

#### Scenario: 带基类 + 接口约束的泛型函数
- **WHEN** 编译 `void F<T>(T x) where T: Animal + IDisplay { ... }`
- **THEN** type_param `T` 的 `constraint_flags` bit 2 (HasBaseClass) 为 1，`base_class_name_idx` 指向 `Animal`，interface_count=1

#### Scenario: 带 class flag 约束
- **WHEN** 编译 `void F<T>(T x) where T: class { ... }`
- **THEN** type_param `T` 的 `constraint_flags` bit 0 (RequiresClass) 为 1，interface_count=0，无 base_class

#### Scenario: 无约束泛型（L3-G1 兼容）
- **WHEN** 编译 `T Identity<T>(T x) { return x; }`
- **THEN** type_param `T` 的 `constraint_flags` = 0，interface_count = 0

#### Scenario: 泛型类带约束
- **WHEN** 编译 `class SortedList<T> where T: IComparable<T> { ... }`
- **THEN** zbc TYPE section 中该类的 type_param `T` 带 interface_count=1，指向 IComparable

### Requirement: Round-trip 保真

zbc writer 写出的约束元数据，reader 读回后与编译器内存中的 `_funcConstraints` / `_classConstraints` 结构一致。

#### Scenario: 约束 round-trip
- **WHEN** `T Max<T>(T a, T b) where T: IComparable<T>` 编译 → 写 zbc → 读 zbc
- **THEN** 读回的 IrFunction.TypeParamConstraints 与原 IrFunction.TypeParamConstraints 相等（按值比较 class/interface/flag）

#### Scenario: 多约束组合 round-trip
- **WHEN** `void F<T>(T x) where T: Animal + IDisplay + IEquatable<T>` round-trip
- **THEN** 读回 bundle 含 BaseClass=Animal, Interfaces=[IDisplay, IEquatable]

### Requirement: VM 加载时读取约束

Rust VM loader 必须把 zbc SIGS / TYPE section 中的约束信息装入 `Function.type_param_constraints` / `TypeDesc.type_param_constraints`。

#### Scenario: VM 加载带约束的泛型函数
- **WHEN** VM 加载编译自 `T Max<T>(T a, T b) where T: IComparable<T>` 的 zbc
- **THEN** `Module.functions` 中 Max 的 `type_param_constraints` 非空，包含 interface=IComparable

#### Scenario: VM 加载带约束的泛型类
- **WHEN** VM 加载编译自 `class Sorted<T> where T: IComparable<T>` 的 zbc
- **THEN** `TypeDesc.type_param_constraints` 非空，与 type_params 对齐

### Requirement: 加载时结构校验（verify pass）

VM 加载完成后运行 verify pass，确认每个约束引用的 class/interface 在 type_registry 或标准命名空间中可找到。

#### Scenario: 约束引用有效
- **WHEN** 加载含 `where T: IComparable<T>` 的 zbc，且 IComparable 在 Std 命名空间可用
- **THEN** verify pass 通过

#### Scenario: 约束引用无效类
- **WHEN** 加载含 `where T: NonExistentBase` 的 zbc（手工构造，跳过 TypeChecker）
- **THEN** verify pass 报错 `InvalidConstraintReference`，加载失败

#### Scenario: Std 命名空间引用延迟解析
- **WHEN** 加载主模块依赖 `Std.IComparable`，verify pass 时 lazy loader 尚未加载 Std
- **THEN** verify pass 对 `Std.*` 前缀引用放行（懒加载时再校验）

## IR Mapping

- IR 层新增 `IrConstraintBundle` record（C# 侧）/ `ConstraintBundle` struct（Rust 侧）
- SIGS / TYPE section 新字段布局（见 proposal.md）
- ZASM 文本：`.constraints T: Animal + IDisplay + class`（class/struct 作 keyword 显示）

## Pipeline Steps

- [x] Lexer / Parser（无改动）
- [x] TypeChecker（无行为改动；IrGen 消费已有 `_funcConstraints` / `_classConstraints`）
- [ ] IR Codegen（填充 IrFunction/IrClassDesc.TypeParamConstraints）
- [ ] zbc Writer / Reader（新字段）
- [ ] ZpkgReader（同步跳过/解析）
- [ ] VM bytecode struct 扩展
- [ ] VM loader 读取 + verify pass
