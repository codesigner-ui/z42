# Spec: Package 内多 CU cross-file symbol 共享

## ADDED Requirements

### Requirement: stdlib 从干净状态可 build

清空 `artifacts/z42/libs/*.zpkg` 和 `src/libraries/*/dist/` 后，
`./scripts/build-stdlib.sh` 一次完成所有 5 个 stdlib 包 build。

#### Scenario: 完全干净状态 build
- **GIVEN** `rm -rf artifacts/z42/libs/*.zpkg src/libraries/*/dist`
- **WHEN** `./scripts/build-stdlib.sh`
- **THEN** 输出 `build-stdlib: 5 succeeded, 0 failed`
- **AND** z42.core.zpkg / z42.io.zpkg / z42.math.zpkg / z42.text.zpkg / z42.collections.zpkg 全部产出

### Requirement: 包内 cross-file class 继承

同 stdlib 包内 A.z42 的 class 继承 B.z42 的 class，编译应通过。

#### Scenario: ArgumentException 继承 Exception
- **GIVEN** `z42.core/src/Exception.z42` 含 `class Exception { ... }`
- **AND** `z42.core/src/Exceptions/ArgumentException.z42` 含
  `class ArgumentException : Exception { ArgumentException(string m) : base(m) {} override string ToString() {...} }`
- **WHEN** `build-stdlib.sh` 编译 z42.core
- **THEN** 编译通过，无 `no matching virtual or abstract method in base class` 错误
- **AND** ArgumentException 能访问 base.Message 字段

### Requirement: 包内 cross-file 接口 generic constraint

同包内 A.z42 类的 generic constraint 引用 B.z42 接口。

#### Scenario: List<T> where T: IEquatable<T>
- **GIVEN** `z42.core/src/IEquatable.z42` 含 `interface IEquatable<T> { ... }`
- **AND** `z42.core/src/Collections/List.z42` 含
  `class List<T> where T: IEquatable<T> + IComparable<T> { ... }`
- **WHEN** 编译 z42.core
- **THEN** 编译通过，无 `constraint on T must be a class or interface` 错误

### Requirement: 包内 cross-file member 引用

A.z42 的方法访问 B.z42 类的 fields / methods（通过继承 / 类型）。

#### Scenario: 子类访问基类字段
- **GIVEN** Exception 含 `public string Message;`
- **AND** ArgumentException 子类 `override string ToString() { return "..." + this.Message; }`
- **WHEN** 编译
- **THEN** 编译通过，`this.Message` 类型推断为 string

### Requirement: intraSymbols 仅本次 build 使用，不写盘

#### Scenario: 不污染 zpkg TSIG
- **GIVEN** PackageCompiler 完成 build 后产出 zpkg
- **WHEN** 检查 zpkg TSIG 的 `interfaces` / `classes` 列表
- **THEN** 仅含**本包内**导出的 declarations（与 sem.Interfaces 同步）
- **AND** 不重复包含外部 imported 的 declarations（与既有 ExtractInterfaces 行为一致）

## MODIFIED Requirements

### Requirement: PackageCompiler 多 CU 编译流程

**Before**：每个 CU 独立 CompileFile，仅靠 `tsigCache.LoadAll()` 看到外部
imported；同包内文件互相不可见。

**After**：先 Phase 1 collect 同包所有 CU 的 declarations 到 intraSymbols；
然后 Phase 2 每个 CU 用 `combined = externalImported ∪ intraSymbols` 编译。

intraSymbols 与 externalImported 合并时，**intraSymbols 优先**（本包内
的 declarations 覆盖外部同名导入）。

### Requirement: ImportedSymbols 合并 helper

**Before**：`ImportedSymbolLoader.Load(modules, usings)` 仅从 zpkg modules
构造。
**After**：新增静态合并方法 `Combine(left, right)`，`right` 优先；签名待
design.md 确定。

## IR Mapping

不引入新 IR 指令；纯 Pipeline 数据流改动。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser — 不涉及
- [x] **PackageCompiler 改造** — 两阶段编译流程
- [ ] TypeChecker — 仅消费合并后 imported（已有参数路径）
- [ ] IR Codegen — 不涉及
- [ ] VM — 不涉及
- [ ] zbc / TSIG 格式 — 不变
