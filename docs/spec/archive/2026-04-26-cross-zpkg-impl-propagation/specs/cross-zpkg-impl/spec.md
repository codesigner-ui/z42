# Spec: cross-zpkg `impl` 块传播

## ADDED Requirements

### Requirement: 编译器把 impl 块写入 zpkg IMPL section

#### Scenario: 同 CU 本地 impl 写入 IMPL
- **WHEN** 一个 z42 编译单元包含 `impl Trait for Type { ... }`，target 和 trait 任意（local 或 imported）
- **THEN** 编译产物 `.zpkg` 包含一个 `IMPL` section
- **AND** IMPL section 含一条 record：`{target_fq_name, trait_fq_name, trait_type_args, methods}`
- **AND** target/trait 名都是 **fully-qualified**（imported 用源 zpkg 命名空间，local 用本包命名空间）
- **AND** methods 的 body 不在 IMPL section，仍写入 MODS section（函数符号 = `{target_fq}.{method_name}`）

#### Scenario: 无 impl 块的 zpkg
- **WHEN** zpkg 不包含任何 impl 块
- **THEN** zpkg 不写 IMPL section（或写空 section，二选一保持简单）

#### Scenario: 多个 impl 块给同一 target
- **WHEN** 一个 CU 含 `impl I1 for T { ... }` 和 `impl I2 for T { ... }`
- **THEN** IMPL section 含两条 record，分别记录 trait I1 / I2 + 各自方法

### Requirement: 消费者读取上游 IMPL section 并合并

#### Scenario: 跨 zpkg impl 让 trait 检查通过
- **GIVEN** z42.numerics zpkg 包含 IMPL record `{target=Std.int, trait=Std.INumber, methods=[op_Add(int,int)→int]}`
- **WHEN** 用户代码 `using z42.core; using z42.numerics;` 写 `where T: INumber<int>` + 调用方传 `int`
- **THEN** TypeChecker 接受（`int` 满足 `INumber<int>` 约束）
- **AND** `int.op_Add(a, b)` 类型检查通过（target class 的 Methods 含 op_Add）

#### Scenario: 缺失上游 zpkg 时 trait 不可见
- **GIVEN** 用户代码只 `using z42.core;`（不 using z42.numerics）
- **WHEN** 写 `where T: INumber<int>` + 传 `int`
- **THEN** TypeChecker 报错：`int` 不满足 `INumber<int>` 约束（与今天 L3-Impl1 同 CU 之外不可见行为一致）

#### Scenario: 同名 impl 方法冲突
- **GIVEN** z42.numerics 给 int 加 `op_Add`，z42.numerics2 也给 int 加 `op_Add`（两包同时被 using）
- **WHEN** ImportedSymbolLoader 合并第二包 IMPL
- **THEN** 第二条记录被 first-wins 跳过（不报错，与 `MergeImported` 既有 TryAdd 语义一致）

#### Scenario: impl 方法与 target class 已声明方法冲突
- **GIVEN** z42.core 的 `int` struct 已有方法 `Equals`，z42.someext 试图 `impl IFoo for int { Equals(...) }`
- **WHEN** ImportedSymbolLoader 合并 impl
- **THEN** Methods 字典中 target 已有的方法 wins；新方法跳过

### Requirement: VM 调用跨 zpkg impl 方法

#### Scenario: 实例方法调用解析到 impl 方法
- **GIVEN** z42.numerics 提供 `impl INumber<int> for int { static int op_Add(int,int) }`
- **AND** 用户代码 `using z42.core; using z42.numerics;`
- **WHEN** 运行时执行 `int.op_Add(3, 5)`
- **THEN** VM 通过函数符号 `Std.int.op_Add` 在 `func_index` 中找到 z42.numerics 的方法 body
- **AND** 调用返回 `8`

#### Scenario: 接口约束泛型代码调用
- **WHEN** 运行时执行 `Sum<int>(items)` where Sum's body uses `T.op_Add`，T 泛型实例化为 int
- **THEN** VCall("op_Add") 通过 `primitive_class_name(int) = "Std.int"` + method `"op_Add"` → `Std.int.op_Add` → z42.numerics body

### Requirement: zbc 格式版本 + 兼容性

#### Scenario: zbc 版本号 bump
- **WHEN** L3-Impl2 落地
- **THEN** `ZbcWriter.VersionMinor` 从 `0.7` bump 到 `0.8`
- **AND** Rust VM decoder 接受 0.8，拒绝旧 0.7（无兼容路径，pre-1.0 规则）
- **AND** golden test 通过 `regen-golden-tests.sh` 全部重生

#### Scenario: 旧 zbc 不可读
- **WHEN** 加载 0.7 zbc
- **THEN** VM 报错 `zbc version mismatch: expected 0.8, got 0.7`

## MODIFIED Requirements

### Requirement: ExportedTypeExtractor 输出范围

**Before:** 只输出 classes / interfaces / enums / functions，跳过 imported classes。

**After:** 同时输出 cu.Impls 列表（即使 target 是 imported），写入新 IMPL section。
extractor 调用方 `ExtractImpls(cu)` 产出 `List<ExportedImplDef>`。

### Requirement: ImportedSymbolLoader 加载阶段

**Before:** 两阶段：
- Phase 1: 骨架登记（class / interface 空成员）
- Phase 2: 成员填充（in-place mutate）

**After:** 三阶段：
- Phase 1: 骨架登记
- Phase 2: 成员填充
- **Phase 3 (NEW)**: impl 合并 — 遍历所有 IMPL records，对每条 `{target, trait, methods}`：
  - 在 Phase 2 完成的 imported `Z42ClassType.Methods` 字典里 in-place add 方法
    （TryAdd，first-wins）
  - 在 `_classInterfaces[target]` 列表里追加 trait（去重）

### Requirement: IrGen impl 方法函数符号命名

**Before:** [`IrGen.cs:132`](src/compiler/z42.Semantics/Codegen/IrGen.cs#L132)
用 `QualifyName(targetNt.Name)` —— 总是用本包命名空间。

**After:** 用 `QualifyClassName(targetNt.Name)` —— 若 target 是 imported，
返回 target 的源命名空间（如 `Std.int`）；若 local，返回本包命名空间。
确保 VM `func_index` 注册到正确的 FQ 名。

## Pipeline Steps

- [x] Lexer — 无变更
- [x] Parser / AST — 无变更（cu.Impls 已存在）
- [ ] TypeChecker — `ExportedTypeExtractor.ExtractImpls` 新增；
  `ImportedSymbolLoader` 加 Phase 3 merge
- [ ] IR Codegen — `IrGen.cs:132` 修复 `QualifyClassName`
- [ ] zbc/zpkg format — IMPL section 序列化 / 反序列化（C# writer + reader + Rust decoder）
- [ ] VM interp — 无功能变更（VM 已能 dispatch；只需能跳过 IMPL section）

## IR Mapping

无新 IR 指令。impl 方法 body 用现有 `IrFunction` 表示，函数名 = `{target_fq}.{method}`。
新 IMPL section 是 zbc / zpkg 元数据层，不进入 IR 指令流。
