# Proposal: PackageCompiler 多 CU 包内 cross-file symbol 共享

## Why

实施 `fix-generic-interface-dispatch` 时清空 zpkg 缓存（试图让新加的 TypeParams
完全 propagate）暴露了 stdlib bootstrap 的既有缺陷：

**z42.core 包内多文件无法从干净状态启动 build**：

```
z42.core/src/Exception.z42                    ← 基类
z42.core/src/Exceptions/ArgumentException.z42 ← 继承 Exception (跨文件)
z42.core/src/IEquatable.z42
z42.core/src/Collections/List.z42             ← where T: IEquatable<T> (跨文件)
z42.core/src/Collections/Dictionary.z42       ← where K: IEquatable<K> (跨文件)
```

错误示例：
- `error E0411: ArgumentException.ToString: no matching virtual or abstract method in base class`
- `error E0402: constraint on T must be a class or interface, got IEquatable`
- `error E0402: type ArgumentException has no member Message`

[PackageCompiler.TryCompileSourceFiles](src/compiler/z42.Pipeline/PackageCompiler.cs#L295)
多 CU 编译用 for-loop 每个文件**独立** CompileFile：

```csharp
foreach (var sourceFile in sourceFiles)
{
    var unit = CompileFile(sourceFile, depIndex, tsigCache);
    ...
}
```

`MergeImported` 通过 `tsigCache.LoadAll()` 加载**外部已 build 的 zpkg** —— **同
包内**未编译完的文件**互不可见**。

之前 build 总能成功是因为开发者本地有**残留 zpkg cache**（`artifacts/` 和
`dist/` 都 gitignored，cache 一直存在）。一旦清空 cache，stdlib 自启动失败。

按 [.claude/rules/workflow.md "修复必须从根因出发"](.claude/rules/workflow.md#修复必须从根因出发-2026-04-26-强化)，
不能依赖 stale cache 跑通 build；必须从源头让多 CU 共享 symbol。

## What Changes

### 设计：包内 intra-package symbol pre-collection

```
旧流程                                  新流程
──────                                  ──────
foreach cu in sourceFiles:              # Phase 1: parse + Pass-0 collect 所有 CU
  CompileFile(cu, tsigCache):           parsedCus = parse all source files
    parse → cu                          intraSymbols = ImportedSymbols.Empty()
    imported = tsigCache.LoadAll()      foreach cu in parsedCus:
    sem = TypeCheck(cu, imported)         tmpCollector = new SymbolCollector()
    ir = Codegen(cu, sem)                 tmpCollector.MergeImported(externalImported)
    zbc = ZbcWriter(ir)                   tmpCollector.Collect(cu, externalImported)
                                          intraSymbols.MergeFrom(tmpCollector)

                                        # Phase 2: 每个 CU 编译 with combined imported
                                        foreach cu in parsedCus:
                                          combined = merge(externalImported, intraSymbols)
                                          CompileFile(cu, combined):
                                            sem = TypeCheck(cu, combined)
                                            ir = Codegen(cu, sem)
                                            zbc = ZbcWriter(ir)
```

intraSymbols 是同包内所有 CU 的 **declarations**（不含 method bodies），可以
作为 ImportedSymbols 注入每个 CU 编译。

### 关键不变量

- 每个 CU 的 sem 仍独立（body binding / IR 各自做）
- intraSymbols 仅含 declarations（class shape、interface methods sig、enum、
  free function sig），不含具体方法 body 或 IR
- 跨 CU 同名声明：first-wins（与现有 ImportedSymbols 合并语义一致）
- intraSymbols 不写入 zpkg TSIG（仅本次 build 使用，next build 从 zpkg 加载）

## Scope

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | edit | `TryCompileSourceFiles` 改两阶段；新增 `BuildIntraPackageSymbols` 辅助；`CompileFile` 增 `intraSymbols` 参数 |
| `src/compiler/z42.Pipeline/PipelineCore.cs` | edit | `Compile` 接受合并后 imported（已有 imported 参数；只需在 PackageCompiler 端 merge） |
| `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` | edit | 新增 `MergeIntoImported(ImportedSymbols target, ImportedSymbols delta)` 或 `Combine(a, b)` 静态方法 |
| `src/compiler/z42.Tests/PackageCompilerTests.cs` 或新建 | add | 多 CU 包内 cross-file 测试（如 A.z42 含 IFoo，B.z42 实现 IFoo） |
| `docs/design/compiler-architecture.md` | edit | 新增"多 CU 包内 symbol 共享"章节 |

## Out of Scope

- TypeChecker / IrGen 内部逻辑改动（每个 CU 独立 binding 不变）
- zpkg TSIG 序列化格式变更（intraSymbols 仅内存使用，不写盘）
- 跨包 cross-reference（仍走 tsigCache 加载 zpkg）
- 循环依赖检测（同包内 A→B→A 仍合法，因 Pass-0 仅收集 declarations）

## Open Questions

- [x] 共享方式：合并 dict vs 共享 SymbolCollector 实例 — **合并 dict（intraSymbols）**，保持每个 CU 独立 SymbolTable
- [x] intraSymbols 是否写盘 — **不写**（仅本次 build 内存使用）
- [x] CU 编译顺序是否仍保持文件名字母序 — **保持**（intraSymbols 已 pre-collected，顺序不影响结果）
- [x] CU 内部 declarations 与 externalImported 的优先级 — **本包优先**（intraSymbols 覆盖 externalImported 的同名条目；与 SymbolCollector.MergeImported first-wins 不同：本包总是优先）

## Blocks / Unblocks

- **Unblocks**：
  - stdlib 干净状态 build 工作（即使无 zpkg cache）
  - `fix-generic-interface-dispatch` 可恢复推进（已 stash）
  - 未来任何 stdlib 包内 cross-file 重构无 bootstrap 障碍
- **Blocks**：当前 fix-generic-interface-dispatch（已 stash 保留）
