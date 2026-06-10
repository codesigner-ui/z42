# Spec: import — 跨包 import 消费侧（MVP）

## ADDED Requirements

### Requirement: stdlib-using 源可编译可执行

#### Scenario: hello-stdlib 端到端
- **WHEN** `z42c build` 编译 `using Std; void Main(){ Console.WriteLine("hi"); }` 工程（Z42_LIBS 含 stdlib）
- **THEN** 产出 zpkg 经 z42vm 直跑 stdout 输出 hi（exit 0）

#### Scenario: 静态调用 FQ 目标
- **WHEN** 上述源 codegen
- **THEN** CallInstr.Func = DepIndex 的 QualifiedName（如 `Std.Console.WriteLine`），IMPT 段含该名，DEPS 段含 (z42.core.zpkg, 使用的 ns 列表)

### Requirement: byte-identical

#### Scenario: hello-stdlib 全文件对账
- **WHEN** 同工程分别经 z42c 与 C# CLI（--strip-symbols=false）构建
- **THEN** 两 zpkg **全文件逐字节一致**（xtask gate 第 3 工程常驻）

### Requirement: 确定性

#### Scenario: libs 扫描序
- **WHEN** DepScan/nsMap 扫描 libs 目录
- **THEN** prelude-first + Ordinal 排序（重复 ns/键 first-wins 结果跨 OS 稳定）

#### Scenario: 未声明 using 不激活
- **WHEN** 源无 `using Std.Net;`
- **THEN** 提供 Std.Net 的非 prelude 包不进 ImportedSymbols（符号不可见）

## Pipeline Steps
- [ ] ZpkgReader 子集（project）
- [ ] DependencyIndex + DepScan（ir + pipeline）
- [ ] ImportedSymbolLoader Phase1/2 子集（semantics）
- [ ] TypeChecker/ExprEmitter/DEPS 接线
- [ ] driver build 组装 + xtask e2e
