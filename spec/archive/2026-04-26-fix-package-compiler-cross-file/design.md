# Design: PackageCompiler 多 CU cross-file symbol 共享

## Architecture

```
新增数据流
──────────

PackageCompiler.BuildTarget(...)
  │
  ├── ScanLibsForNamespaces → tsigCache (外部 zpkg 索引)
  ├── BuildDepIndex
  │
  ├── 〔NEW〕Phase 1: Pre-collect intra-package declarations
  │   parsedCus = []
  │   foreach sourceFile in sourceFiles:
  │     cu = parse(sourceFile)
  │     parsedCus.Add(cu)
  │
  │   externalImported = ImportedSymbolLoader.Load(tsigCache.LoadAll(), allNs)
  │
  │   intraSymbols = ImportedSymbols.Empty()
  │   foreach cu in parsedCus:
  │     // 仅 Pass 0 collect declarations，不 bind body
  │     localSymbols = SymbolCollector.Collect(cu, externalImported)
  │     intraSymbols.MergeFrom(localSymbols, sourceCuNamespace)
  │
  ├── 〔CHANGED〕Phase 2: Type-check + Codegen with combined imports
  │   combined = ImportedSymbols.Combine(externalImported, intraSymbols)
  │              // intraSymbols 优先（本包覆盖外部同名）
  │   foreach cu in parsedCus:
  │     unit = CompileFile(cu, depIndex, combined)
  │     units.Add(unit)
  │
  └── BuildPacked / WriteZpkg
```

## Decisions

### Decision 1: intraSymbols 类型选 ImportedSymbols 复用

**问题**：intraSymbols 用什么数据结构？

**决定**：复用现有 `ImportedSymbols` record（有 Classes / Interfaces /
Functions / EnumConstants / EnumTypes / ClassNamespaces / ClassConstraints /
FuncConstraints / ClassInterfaces 字段）。

**理由**：
- ImportedSymbols 已经是 SymbolCollector.MergeImported 的输入；新代码无需
  适配新数据结构
- 同 stdlib 加载 zpkg → ImportedSymbols 路径对齐（语义一致）

### Decision 2: intraSymbols 来源：用 SymbolCollector Pass 0 输出

**问题**：怎么从 parsed CU 提取 declarations？

**决定**：每个 cu 跑一次 `SymbolCollector.Collect(cu, externalImported)`，
取得 SymbolTable，然后**截取 declarations** 转成 ImportedSymbols。

**理由**：
- SymbolCollector 已实现完整 Pass 0（CollectInterfaces / CollectClasses /
  CollectImpls / CollectFunctions / CollectEnums），不需要新代码
- Pass 0 只填 declarations 不 bind body，开销小
- Pass 1 (TypeCheck body) 在 Phase 2 才跑，每个 CU 重新跑

潜在问题：每个 cu Pass-0 会跑两次（一次 Phase 1 收集 intraSymbols，一次
Phase 2 完整编译）。开销可接受（Pass 0 比 Pass 1 + IR 快得多），且避免
状态共享带来的复杂度。

### Decision 3: 同包内 first-seen vs externalImported 优先

**问题**：合并 externalImported 和 intraSymbols 时谁优先？

**决定**：**intraSymbols 优先**（本包内的覆盖外部同名）。

**理由**：
- 编译同包文件 A 时，A 引用 B（同包），B 应该是本包内的最新 declaration，
  而非外部 zpkg 的旧版（可能是上次 build 的 stale）
- 与"先来先得"的 SymbolCollector.MergeImported（first-wins）不同 — 这里
  是**第二阶段合并**，已经知道哪些来自本包

### Decision 4: Phase 1 SymbolCollector 用 externalImported 作 base

**问题**：Phase 1 收集每个 cu 的 declarations 时，要不要也注入
externalImported？

**决定**：**注入 externalImported**（与 Phase 2 一致）。

**理由**：
- 某些 declaration 可能引用 externalImported（如 cross-package 类引用）
- 让 Pass 0 看到完整可见 symbol set，避免因 external miss 误报错
- 注入 externalImported 不影响 intraSymbols 收集 — intraSymbols 只取**本
  CU 新增**的 declarations，不从 externalImported 重复（first-wins TryAdd
  策略）

### Decision 5: ImportedSymbolLoader.Combine helper

签名：

```csharp
public static ImportedSymbols Combine(
    ImportedSymbols externalLow, ImportedSymbols intraHigh)
{
    // intraHigh 优先（本包覆盖外部同名）
    var classes    = new Dictionary<string, Z42ClassType>(externalLow.Classes);
    foreach (var (k, v) in intraHigh.Classes) classes[k] = v;       // override

    var funcs      = new Dictionary<string, Z42FuncType>(externalLow.Functions);
    foreach (var (k, v) in intraHigh.Functions) funcs[k] = v;

    var interfaces = new Dictionary<string, Z42InterfaceType>(externalLow.Interfaces);
    foreach (var (k, v) in intraHigh.Interfaces) interfaces[k] = v;

    // ... 其余字段同模式（enumConsts / enumTypes / classNs /
    //     classConstraints / funcConstraints / classInterfaces）

    return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes,
        classNs, classConstraints, funcConstraints, classInterfaces);
}
```

### Decision 6: 提取 intraSymbols from SymbolTable

`SymbolTable` 不是 `ImportedSymbols`。需要 helper 转换：

```csharp
private static ImportedSymbols SymbolTableToImported(
    SymbolTable symbols, string namespaceName,
    HashSet<string> externalImportedClassNames)
{
    // 仅取 **本 CU 新增** 的 declarations（不在 externalImportedClassNames 中）。
    var classes = new Dictionary<string, Z42ClassType>();
    foreach (var (n, ct) in symbols.Classes)
        if (!externalImportedClassNames.Contains(n))
            classes[n] = ct;

    // Interfaces 同样过滤（symbols.Interfaces 含 imported + local）
    // ...
}
```

或者更简单：用 `symbols.ImportedClassNames` 过滤掉 imported 的，剩下的就是
本 CU 新增。**实际上 ImportedClassNames 只跟踪 class，interface / function
没有同样的 tracking。**

**简化**：每个 CU 编译时新建 SymbolCollector，不注入 externalImported（即
`Collect(cu, null)`）。这样 SymbolTable.Interfaces / Classes 都只含本 CU 的
declarations（无外部 noise）。

revised Phase 1：

```csharp
foreach (var cu in parsedCus)
{
    var localCollector = new SymbolCollector(diags);
    var localSymbols   = localCollector.Collect(cu, imported: null);
    intraSymbols       = ImportedSymbolLoader.Combine(intraSymbols,
        SymbolTableToImported(localSymbols, cu.Namespace));
}
```

但是 cu 可能引用同包其他 cu 的 declarations 才能 collect 通过（class
inherits across files）— 实际上 SymbolCollector Pass 0 不验证 class
hierarchy（abstract method override 检查在 Pass 0 后），所以 Pass 0 应该
能 work。

**保守策略**：Phase 1 注入 externalImported（避免 cross-package miss），但
extract intraSymbols 时**过滤掉**外部 imported（用
`localSymbols.ImportedClassNames` + 类似 ImportedInterfaceNames 跟踪）。

### Decision 7: 添加 ImportedInterfaceNames 跟踪

为了过滤 intraSymbols，SymbolCollector 需要跟踪哪些 interface 是 imported
的（与 ImportedClassNames 对称）。

```csharp
// SymbolCollector
internal readonly HashSet<string> _importedInterfaceNames = new();

// MergeImported
foreach (var (name, it) in imported.Interfaces)
{
    if (_interfaces.TryAdd(name, it))
        _importedInterfaceNames.Add(name);
}

// SymbolTable
public IReadOnlySet<string> ImportedInterfaceNames { get; }
```

类似处理 functions / enums。

实际上更彻底：**新增 SymbolTable.ExtractIntraSymbols()** 方法，输出仅含本
包 declarations 的 ImportedSymbols。

## Implementation Notes

### TryCompileSourceFiles 改造

```csharp
static List<CompiledUnit>? TryCompileSourceFiles(
    IReadOnlyList<string> sourceFiles,
    DependencyIndex       depIndex,
    TsigCache?            tsigCache = null)
{
    // External imports from already-built zpkgs (cross-package).
    var externalImported = LoadExternalImported(tsigCache);

    // Phase 1: parse all + pre-collect intra-package declarations
    var parsedCus = new List<(string file, CompilationUnit cu)>();
    foreach (var sourceFile in sourceFiles)
    {
        var cu = ParseFile(sourceFile);
        if (cu != null) parsedCus.Add((sourceFile, cu));
    }

    var intraSymbols = ImportedSymbolLoader.Empty();
    foreach (var (_, cu) in parsedCus)
    {
        var diags = new DiagnosticBag();
        var collector = new SymbolCollector(diags);
        var symbols = collector.Collect(cu, externalImported);
        var intra = ExtractIntraSymbols(symbols, cu.Namespace);
        intraSymbols = ImportedSymbolLoader.Combine(intraSymbols, intra);
    }

    // Phase 2: full compile each CU with combined imports
    var combined = ImportedSymbolLoader.Combine(externalImported, intraSymbols);
    var units = new List<CompiledUnit>();
    int errors = 0;
    foreach (var (sourceFile, cu) in parsedCus)
    {
        var unit = CompileFileWithImported(sourceFile, cu, depIndex, combined);
        if (unit is null) { errors++; continue; }
        units.Add(unit);
    }
    if (errors > 0) { ...; return null; }
    return units;
}
```

### 性能影响

- Parse: 每个 cu 仍只 parse 一次（Phase 1 parse + cache 给 Phase 2 用）
- Symbol collection: 每个 cu 跑 2 次 Pass 0（Phase 1 一次 + Phase 2 一次）—
  Pass 0 是简单字典构造，开销低
- Body binding + IR: 每个 cu 跑 1 次（Phase 2 仅）— 与原来一样

总开销增加 ≈ 1 次 Pass 0 / CU。stdlib 30+ CU 总额外 < 100ms。

### 兼容性

- 只在 PackageCompiler 路径生效；SingleFileCompiler 保持原样（单文件不需要
  intra-package 共享）
- 现有 unit tests 不受影响（仍走 Single-CU 路径）

## Testing Strategy

### 单元测试 / 端到端测试

- 测试用例：构造**临时**多文件 stdlib mock（如临时 z42.testpkg/）
  - file A.z42: `interface IFoo { ... }`
  - file B.z42: `class Bar : IFoo { ... }`
  - 清空 zpkg cache → 调 PackageCompiler.BuildTarget → 应一次成功

### 端到端验证（关键）

- 清空 `artifacts/z42/libs/*.zpkg` 和 `src/libraries/*/dist/`
- 跑 `./scripts/build-stdlib.sh` → 应 5/5 success
- 全量回归：dotnet test / test-vm.sh / cargo test 全绿

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| Phase 1 Pass-0 collect 失败（cu 引用未见的同包 type） | 中 | Pass 0 注入 externalImported；PassThroughErrors 给 Phase 2 报错 |
| intraSymbols 提取漏算（例如 import* 收集 vs 本地）| 中 | `ExtractIntraSymbols` 用 `ImportedClassNames` / 新增 `ImportedInterfaceNames` 过滤 |
| Phase 1 + Phase 2 看到的 sem 不一致（lexer 改 cu 实例）| 低 | parsedCus 缓存共享，每个 cu 实例只 parse 一次 |
| TsigCache 同时用于 external + intra | 低 | intraSymbols 不写 tsigCache；不冲突 |
