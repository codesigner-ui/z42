# Spec: ImportedSymbolLoader 两阶段加载

## ADDED Requirements

### Requirement: stdlib 类型导入不降级 self/forward-reference

`ImportedSymbolLoader.Load(modules, usings)` 完成后，所有已导入类的字段类型 /
方法签名中引用其他已导入类型的位置，必须是 `Z42ClassType` 或 `Z42InterfaceType`
对应的实际对象，**不得降级为 `Z42PrimType(name)`**。

#### Scenario: 类字段 self-reference（同类型字段）
- **GIVEN** stdlib 含 `class Exception { Exception InnerException; }`
- **WHEN** `ImportedSymbolLoader.Load(...)` 加载 stdlib TSIG
- **THEN** `classes["Exception"].Fields["InnerException"]` 类型 == `Z42ClassType("Exception")`
- **AND** 该 ClassType 引用与 `classes["Exception"]` 同一个对象（或等价 record）

#### Scenario: 跨类 forward-reference
- **GIVEN** stdlib 含两个类 `A` 和 `B`，`A.field: B` 字段引用 `B`
- **AND** TSIG 模块顺序导致 `A` 在 `B` 前被处理
- **WHEN** Load 完成
- **THEN** `classes["A"].Fields["field"]` 类型 == `Z42ClassType("B")`
- **AND** 不依赖于模块加载顺序

#### Scenario: 类方法签名包含同类型参数 / 返回值
- **GIVEN** `class Exception { Exception Wrap(Exception inner); }`
- **WHEN** Load 完成
- **THEN** `classes["Exception"].Methods["Wrap"].ParamTypes[0]` == `Z42ClassType("Exception")`
- **AND** `classes["Exception"].Methods["Wrap"].ReturnType` == `Z42ClassType("Exception")`

#### Scenario: 接口字段 / 方法引用其他接口
- **GIVEN** `interface IEnumerable<T> { IEnumerator<T> GetEnumerator(); }`
  和 `interface IEnumerator<T> { ... }` 都被导入
- **WHEN** Load 完成
- **THEN** `interfaces["IEnumerable"].Methods["GetEnumerator"].ReturnType`
  实际为 `Z42InterfaceType("IEnumerator")` 对应对象（带 TypeArgs 或 generic
  param 视具体实现）

### Requirement: 真正未知类型仍降级为 Z42PrimType

非 stdlib 已知 class / interface / primitive 的类型名（拼写错误 / 未声明
依赖等），`ResolveTypeName` 仍返回 `Z42PrimType(name)` 作为 sentinel。
TypeChecker 后续阶段会报错。

#### Scenario: 拼写错误类型名
- **GIVEN** TSIG 字段类型为 `"NoSuchType"` 但没有对应 class / interface
- **WHEN** Load 完成
- **THEN** 该字段类型 == `Z42PrimType("NoSuchType")`
- **AND** TypeChecker 后续阶段使用此类型时报清晰错误（如 E0xxx unknown type）

### Requirement: User code 同类型字段 self-reference assign 通过

#### Scenario: User code 中 Exception.InnerException = inner 编译通过
- **GIVEN** stdlib `Std.Exception` 已加载（按上面 Requirement 不降级）
- **WHEN** 用户代码写 `var inner = new Exception("a"); var outer = new Exception("b"); outer.InnerException = inner;`
- **THEN** TypeChecker 通过（`IsAssignableTo` 检测到 ClassType vs ClassType
  同名兼容）
- **AND** 运行时字段赋值生效（VM ObjNew + FieldSet 已有路径）

### Requirement: 不引入 IsAssignableTo 兼容分支

修复后，`IsAssignableTo` 函数**不得新增任何"PrimType ↔ ClassType 同名桥接"
分支**。修复点必须是数据源头（`ImportedSymbolLoader`），让 `IsAssignableTo`
始终拿到正确类型。

#### Scenario: 验证 IsAssignableTo 改动为零
- **WHEN** 阅读 `git diff Z42Type.cs`（IsAssignableTo 周边代码）
- **THEN** 该函数无任何代码改动（修复全部位于 ImportedSymbolLoader.cs）

## MODIFIED Requirements

### Requirement: ResolveTypeName 签名

**Before:**
```csharp
internal static Z42Type ResolveTypeName(
    string name, HashSet<string>? genericParams = null)
```

**After:**
```csharp
internal static Z42Type ResolveTypeName(
    string name, HashSet<string>? genericParams = null,
    IReadOnlyDictionary<string, Z42ClassType>? classes = null,
    IReadOnlyDictionary<string, Z42InterfaceType>? interfaces = null)
```

新参数可选，向后兼容（旧调用站点行为不变）。Phase 2 调用必须传完整字典。

## IR Mapping

不涉及 IR 改动；本变更纯粹是 TypeChecker 内部数据流修复。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及
- [x] **TypeChecker** — `ImportedSymbolLoader` 重构为两阶段
- [ ] IR Codegen — 不涉及
- [ ] VM — 不涉及
- [ ] zbc 格式 — 不涉及
