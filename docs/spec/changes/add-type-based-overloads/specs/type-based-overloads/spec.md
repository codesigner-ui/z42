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
