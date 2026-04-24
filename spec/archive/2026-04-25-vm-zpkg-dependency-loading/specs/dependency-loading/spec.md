# Spec: VM zpkg Dependency Loading

## ADDED Requirements

### Requirement: zpkg 作为 VM 懒加载的独立单位

VM 启动后，用户代码触发的动态依赖加载以 **zpkg 文件** 为独立单位，而非
namespace 前缀。多个 zpkg 可声明并导出同一 namespace（如 `Std.Collections`），
VM 按需依次加载，合并进统一函数 / 类型注册表。

#### Scenario: 单 namespace 由多个 zpkg 共享
- **WHEN** `z42.core.zpkg` 声明 namespace `Std.Collections`（含 `List<T>` / `Dictionary<K,V>`），
  且 `z42.collections.zpkg` 也声明同一 namespace（含 `Queue<T>` / `Stack<T>`）
- **AND** 用户程序（interp 模式）依次调用 `new List<int>().Add(1)` 和 `new Stack<int>().Push(1)`
- **THEN** 两个 zpkg 都被加载成功；所有函数调用命中；**不报 AmbiguousNamespaceError**

#### Scenario: Call miss 触发懒加载
- **WHEN** 用户代码执行 `Call "Std.Collections.Stack.Push"`
- **AND** 该函数不在已加载模块的 function_table 中
- **THEN** VM 遍历"已声明但未加载"的依赖 zpkg 列表
- **AND** 依次加载，每加载一个就重试函数查找，命中后停止遍历
- **AND** 若遍历完所有候选仍未命中，报 `undefined function Std.Collections.Stack.Push`

#### Scenario: 已加载 zpkg 不重复加载
- **WHEN** 某 zpkg 文件已被加载进 lazy_loader（记录在已加载集合中）
- **THEN** 后续 Call miss 不再尝试加载该 zpkg，直接跳过

### Requirement: 依赖传递（Transitive AssemblyRef）

加载某个 zpkg 后，其自身 metadata 中声明的 `ZpkgDep` 依赖条目展开进
"已声明但未加载"集合，形成传递闭包。对齐 C# assembly transitive reference
语义。

#### Scenario: 传递依赖自动展开
- **GIVEN** 用户 main module 声明依赖 `z42.collections`
- **AND** `z42.collections.zpkg` 自身声明依赖 `z42.core`
- **WHEN** VM 懒加载 `z42.collections.zpkg` 时读取其 `ZpkgDep` 列表
- **THEN** `z42.core` 被加入"已声明"集合
- **AND** 若 `z42.core` 未加载（实际已由隐式 prelude 加载），后续 miss 可触发加载

### Requirement: `.zbc` 主模块依赖推断

单文件 `.zbc` 模块没有 `ZpkgDep` 列表（只有 `.zpkg` 才有）。VM 为 `.zbc`
主模块从现有 `import_namespaces` 字段反查对应 zpkg 文件作为依赖候选。

#### Scenario: .zbc 主模块启动时一次性推断依赖
- **GIVEN** 主模块是 `.zbc`，其 `import_namespaces = ["Std.Collections", "Std.IO"]`
- **WHEN** VM 启动时初始化 LazyLoader
- **THEN** 扫描 libs 目录，把所有声明了这些 namespace 的 zpkg 加入"已声明"集合
- **AND** 这些 zpkg 不立即加载，等待 Call miss 触发

#### Scenario: .zpkg 主模块直接使用 DEPS
- **GIVEN** 主模块是 `.zpkg`，其 `dependencies = [ZpkgDep { file: "z42.collections.zpkg", ... }]`
- **WHEN** VM 启动
- **THEN** 直接把 `z42.collections.zpkg` 加入"已声明"集合

### Requirement: `z42.core` 隐式 prelude 保留

VM 启动时 eager 加载 `z42.core.zpkg`（若存在），不经过懒加载路径。此行为不变。

#### Scenario: z42.core 在启动时加载
- **WHEN** VM 启动
- **AND** libs 目录下存在 `z42.core.zpkg`
- **THEN** 其模块被 merge 进 main module；其 namespace 列表加入"已加载"集合
- **AND** 用户代码可直接调用 `Std.*` 符号无需 `using`

## MODIFIED Requirements

### Requirement: namespace 解析语义

**Before:** `resolve_namespace(ns, ...)` 在多个 zpkg 声明同 namespace 时
`bail!("AmbiguousNamespaceError")`，只允许一对一映射。

**After:** `resolve_namespace(ns, ...)` 返回**所有**声明该 namespace 的 zpkg
列表（`Vec<PathBuf>`）或类似结构；**不再 bail on ambiguous**。
懒加载路径不再使用此函数；仅保留供编译期诊断 / 工具使用。

### Requirement: LazyLoader 触发键

**Before:** `load_namespace(ns: &str)` —— namespace 驱动。

**After:** `load_dependency(zpkg_file: &str)` —— zpkg 文件驱动；
`attempted` 集合改为记录 zpkg 文件名而非 namespace。

### Requirement: 编译期 TsigCache 支持 namespace 跨 zpkg（Scope 扩展）

编译器加载其他包的 TSIG 元数据时，同一 namespace 被多个 zpkg 声明必须全部
加载，不能 first-wins 丢弃。否则 `ImportedSymbolLoader` 拿不到部分类/接口，
`QualifyClassName` 会把未解析的类名降级为 bare 名，生成错误 IR。

#### Scenario: 编译期两个 zpkg 共享 namespace 都被加载
- **GIVEN** `z42.core.zpkg` 和 `z42.collections.zpkg` 都声明 namespace `Std.Collections`
- **AND** 用户代码 `new Stack<int>()` 且不含显式 `using`
- **WHEN** 编译器执行 `TsigCache.LoadAll()` 或 `LoadForUsings(["Std.Collections"])`
- **THEN** 两个 zpkg 的 TSIG 都被加载进 `ImportedSymbols`
- **AND** Stack 类被 `QualifyClassName` 解析为 `Std.Collections.Stack`
- **AND** 生成的 IR 中 `obj.new @Std.Collections.Stack`（FQ 名）

#### Scenario: TsigCache.RegisterNamespace 允许重复登记
- **WHEN** 扫描 libs 目录时同一 namespace 出现在多个 zpkg
- **THEN** `_nsToPaths[ns]` 追加所有 zpkg 路径（List<string>）
- **AND** 不再 `TryAdd` first-wins；不报错

## IR Mapping

本变更不引入新 IR 指令；`Call` 指令语义不变（仍以 FQ func_name 为目标）。
改变的是 **VM Call miss 时的回填查找策略**。

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：
- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及
- [ ] TypeChecker — 不涉及（`using` 语义不变）
- [ ] IR Codegen — 不涉及（生成的 Call / ObjNew 不变）
- [x] **VM interp / 懒加载器** — 核心改动
- [x] **VM 启动流程（main.rs）** — LazyLoader 初始化接入依赖列表
- [ ] VM JIT — 已有 eager 路径，不改动
- [x] **编译期 TsigCache**（scope 扩展）— 对称支持 namespace 跨 zpkg
