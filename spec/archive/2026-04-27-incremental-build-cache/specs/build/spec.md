# Spec: source_hash 增量编译

## ADDED Requirements

### Requirement: source_hash 命中判定

#### Scenario: 完整命中（首次构建后立即重建）
- **WHEN** 第一次完整编译某 member，产物到 `<dist>/<name>.zpkg` + `<cache>/<rel>.zbc`
- **AND** 立即第二次构建（源文件未变）
- **THEN** 所有 sourceFiles 计算的 SHA-256 与上次 zpkg 的 source_hash 表一致
- **AND** 所有文件标记为 cached，跳过 parse / typecheck / irgen
- **AND** 编译日志含 `cached: N/N files`（N=源文件总数）

#### Scenario: 单文件改动
- **WHEN** 上次构建后修改某一 .z42 文件
- **THEN** 该文件 hash 不匹配 → fresh
- **AND** 其他文件 hash 仍命中 → cached
- **AND** 日志含 `cached: (N-1)/N files`

#### Scenario: 上次 zpkg 不存在（首次）
- **WHEN** dist/<name>.zpkg 不存在
- **THEN** 全部文件为 fresh，正常完整编译

#### Scenario: cache zbc 缺失
- **WHEN** zpkg 存在且 hash 匹配，但 cache/<rel>.zbc 文件缺失（被外部清理）
- **THEN** 该文件视为 fresh，重新编译

#### Scenario: 上次 zpkg 中无对应 ExportedModule
- **WHEN** zpkg 含该文件 source_hash 但缺对应 namespace 的 ExportedModule（zpkg 来自旧版本编译器，结构差异）
- **THEN** 该文件视为 fresh

---

### Requirement: cached CU 重建

#### Scenario: 从 cache 重建 CompiledUnit
- **WHEN** 文件命中 cache
- **THEN** 编译器从 `<cache>/<rel>.zbc` 读出 IrModule（`ZbcReader.Read`）
- **AND** 从上次 `<dist>/<name>.zpkg` 中读出对应 namespace 的 ExportedModule（`ZpkgReader.ReadTsig`）
- **AND** 构造 CompiledUnit，无须 AST / SymbolTable / typecheck

#### Scenario: cached CU 的命名空间
- **WHEN** 重建 CU
- **THEN** Namespace 等于 IrModule.Name（与 fresh CU 一致）

---

### Requirement: 跨 cached/fresh CU 符号互引

#### Scenario: fresh CU 引用 cached CU 的类型
- **WHEN** member 含两个文件 A.z42（cached）+ B.z42（fresh），B 用 A 中定义的类型
- **THEN** B 的 typecheck 阶段能解析到 A 的类型
- **AND** 实现路径：cached CU 的 ExportedModule 通过 externalImported 机制注入 SymbolCollector

#### Scenario: cached CU 引用 fresh CU 的类型
- **WHEN** A.z42（cached）引用 B.z42（fresh）的类型
- **THEN** 因 A 未重新 typecheck，依然能工作（A 的 IrModule 已编码 B 类型的引用，运行时由 lazy_loader 解析）

---

### Requirement: --no-incremental 强制全量

#### Scenario: workspace 模式
- **WHEN** `z42c build --workspace --no-incremental`
- **THEN** 跳过 IncrementalBuild 查询，所有文件视为 fresh

#### Scenario: 单工程模式
- **WHEN** `z42c build path.z42.toml --no-incremental`
- **THEN** 同上

---

### Requirement: 增量统计输出

#### Scenario: 每 member 编译后输出命中率
- **WHEN** 编译 member 完成
- **THEN** stderr 输出形如 `cached: 4/5 files (z42.core)`（或 `0/5` 表示首次/全量）
- **AND** 不列出每个文件名（避免日志噪声）

---

## MODIFIED Requirements

### Requirement: BuildOptions 含 Incremental

**Before**：C4a `WorkspaceBuildOrchestrator.BuildOptions` 含 `Selected/Excluded/AllWorkspace/CheckOnly/Release`。

**After**：增加 `Incremental: bool = true`。`BuildCommand` 解析 `--no-incremental` 时设为 false。

### Requirement: TryCompileSourceFiles 流程

**Before**：所有 sourceFiles 进 Phase 1（parse + sharedCollector.Collect），再 Phase 2（typecheck + irgen）。

**After**：先 IncrementalBuild 分流；cached → 重建 CU；fresh → 走原 Phase1+2；最终 results 合并两路。

---

## Pipeline Steps

- [x] Manifest 解析层（不变；增量在 PackageCompiler）
- [x] 编译器入口（IncrementalBuild + TryCompileSourceFiles 改造）
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更（cache 只复用既存 IrModule 形态）。

## 错误码

无新增 WSxxx；增量命中失败 silent fallback 到 fresh。
