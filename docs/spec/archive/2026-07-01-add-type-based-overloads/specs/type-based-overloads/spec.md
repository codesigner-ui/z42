# Spec: type-based 方法重载决议

## ADDED Requirements

### Requirement: 同 arity 不同类型重载共存

#### Scenario: 同名同 arity 多重载注册不冲突
- **WHEN** 类声明 `void Print(int x)` 与 `void Print(string s)`
- **THEN** 两者均注册（type-mangled 键），互不覆盖；皆可被调用

#### Scenario: 唯一/arity-distinct 方法键不变（byte-identity）
- **WHEN** 类只有 `void F(int)` 一个 F，或 `F(int)` 与 `F(int,int)`（arity 不同）
- **THEN** 注册键沿用现状（bare `F` / `F$1`/`F$2`），emit/TSIG 字节与本变更前一致

### Requirement: 按实参类型解析重载

#### Scenario: 精确类型匹配
- **WHEN** `Print(int)`/`Print(string)` 并存，调用 `Print(42)`
- **THEN** 选 `Print(int)`

#### Scenario: 装箱/加宽候选
- **WHEN** 仅 `Print(object)` 存在，调用 `Print(42)`
- **THEN** 选 `Print(object)`（int 装箱可赋值 object）

#### Scenario: 精确优于加宽
- **WHEN** `Print(int)` 与 `Print(object)` 并存，调用 `Print(42)`
- **THEN** 选 `Print(int)`（精确）而非 `Print(object)`（装箱）

#### Scenario: 子类/接口适用
- **WHEN** `Handle(Animal)` 存在，`Dog : Animal`，调用 `Handle(dog)`
- **THEN** 选 `Handle(Animal)`（子类可赋值基类）

### Requirement: 无匹配与歧义诊断

#### Scenario: 无适用重载
- **WHEN** 仅 `Print(string)` 存在，调用 `Print(42)`（int 不可赋值 string）
- **THEN** 报 no-matching-overload 错误，列出候选

#### Scenario: 歧义重载
- **WHEN** 两个候选对实参均适用且 better-conversion 不可比（如两个不同的加宽）
- **THEN** 报 ambiguous-overload 错误，提示显式 cast 消歧

### Requirement: 跨 zpkg 重载解析

#### Scenario: 跨包同 arity 重载
- **WHEN** zpkg A 暴露 `Log(int)` 与 `Log(string)`，zpkg B 调用 `Log("hi")`
- **THEN** B 从 imported TSIG（含参数 TypeName）重算 mangled 键，解析到 `Log(string)`；端到端运行正确

#### Scenario: 无格式 bump
- **WHEN** 本变更前后读写 zpkg
- **THEN** zbc/zpkg 版本号不变；现有无同 arity 重载的 zpkg 字节不漂移

### Requirement: 实例方法同 arity 重载共存（2026-07-01 追加）

#### Scenario: 普通实例方法同 arity 多重载注册不冲突
- **WHEN** 类声明 `void Log(int x)` 与 `void Log(string s)`（均为实例方法）
- **THEN** 两者均注册（type-mangled 键），互不覆盖；按实参类型正确解析调用

#### Scenario: 协议方法永不 mangle
- **WHEN** 类型上存在 `Equals(object?)` 与 `Equals(string)` 两个同 arity 实例方法
  （`Equals` 属协议豁免名单）
- **THEN** 不参与 type-mangle，沿用现状 arity-only 注册（first-wins，不引入新行为）；
  **不**报 `E0408 DuplicateDeclaration`（与静态/非豁免实例方法的重复键检测区分）

#### Scenario: 索引器豁免（不含操作符，2026-07-01 订正）
- **WHEN** 类型定义 `get_Item`/`set_Item`
- **THEN** 不参与 mangle，行为与本变更前一致（TypeChecker 对这两个名字用字面量字符串查找，
  mangle 会导致查找落空）

#### Scenario: 操作符走静态重载决议，不需要豁免（2026-07-01 新增）
- **WHEN** 类型定义 2 个同 arity 的操作符方法（如 `op_Add(Vec,Vec)` 与 `op_Add(Vec,int)`）
- **THEN** 二者作为静态方法参与既有的静态 type-mangle + 决议（非本次 D7 实例扩展的豁免范围）；
  `_bindBinary` 通过 `_resolveOverload` 按实参类型选中正确重载并派发成功

#### Scenario: virtual override 安全共存
- **WHEN** 基类声明 `virtual void Handle(int x)` 与 `virtual void Handle(string s)`
  （同 arity 重载，非豁免），子类各自 `override` 一个
- **THEN** override 与被覆盖方法因签名一致而 mangle 为同一键，vtable slot 正确复用，
  虚派发结果与签名匹配的重载一致

#### Scenario: VM 零改动
- **WHEN** 本变更扩展到实例方法前后
- **THEN** `src/runtime/` 任何文件不变更；mangle 与否完全由 z42c 端协议豁免名单决定

## MODIFIED Requirements

### Requirement: 方法重载决议（从 arity-only 升级）

**Before:** 重载只按实参**个数**解析（`Name$arity`）；同 arity 多重载键冲突，后者覆盖前者，静默丢失。

**After:** 重载按实参**个数 + 类型**解析；同 arity 多重载经 type-mangled 键共存，调用点按
适用性 + 最具体规则择优，无匹配/歧义时报诊断。

## IR Mapping

- **无新 IR 指令、无新 zbc opcode、无 zbc/zpkg 格式 bump**。
- 同 arity 重载 → 各自唯一 type-mangled IR 函数名（additive 字符串）；VM 按名派发不感知重载。
- TSIG 方法键对同 arity 重载从冲突键改为 mangled 键（参数 TypeName 已在记录中，布局不变）。

## Pipeline Steps

- [x] Lexer（无改动）
- [x] Parser / AST（无改动——重载是语义层）
- [x] TypeChecker（候选枚举 + `_resolveOverload` 适用性/最具体/歧义；24 调用点）
- [x] Codegen（IR 名用 mangled 键；ExportedTypeExtractor/ImportedSymbolLoader/DependencyIndex type-aware）
- [ ] VM interp（无改动——纯按名派发）
