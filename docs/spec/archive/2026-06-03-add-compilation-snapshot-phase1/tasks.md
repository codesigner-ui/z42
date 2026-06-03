# Tasks: F2.1 Phase 1 — Compilation 不可变快照 MVP

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor + 加法 API
> 来源：[`docs/review.md`](../../../review.md) Part 6 F2.1

## 变更说明

引入 Roslyn `CSharpCompilation` 风格的不可变快照 wrapper：

```csharp
public sealed class Compilation {
    public CompilationUnit  CompilationUnit { get; }
    public DependencyIndex  DepIndex { get; }
    public LanguageFeatures Features { get; }
    public ImportedSymbols? Imported { get; }

    public SemanticModel?      Model       { get; }       // lazy on first access
    public DiagnosticBag       Diagnostics { get; }       // lazy on first access

    public Compilation WithCompilationUnit(CompilationUnit cu);
    public Compilation WithImported(ImportedSymbols? imported);

    public static Compilation Create(
        CompilationUnit cu,
        DependencyIndex depIndex,
        LanguageFeatures? features = null,
        ImportedSymbols? imported = null);
}
```

Compilation 是 PipelineCore 上方的薄包装 —— 内部委托给现有
`PipelineCore.CheckOnly` 跑 binding，缓存结果。Phase 1 不动 PipelineCore；
不引入 SyntaxTree / 多文件 / References / Emit；只解决"反复对同一 CU 调
CheckOnly 时不重复 typecheck"这一痛点 + codify 不可变契约。

## 原因 / Phase 划分

review.md F2.1 完整版（多 SyntaxTree / References / Emit / 增量重编译）是
7-10 天 P0 工作。当前 z42 没有 SyntaxTree 类型也没有多文件 Compilation，
原始迁移成本很高。

**Phase 1 (本 spec) 焦点**：codify 不可变快照契约 + 提供 caller-side
caching wrapper。当前 PipelineCore.CheckOnly 每次调用都重做完整 typecheck
（即便对同一 CU 多次问）—— Compilation 包装解决这点。

**Phase 2** (独立 spec): SyntaxTree wrapper + 多文件支持
**Phase 3** (独立 spec): References / cross-Compilation 引用
**Phase 4** (独立 spec): incremental 重新绑定（substitute single SyntaxTree
without redoing everything）

## 文档影响

- `docs/review.md` F2.1 / Part 6 P0 状态更新 (🟡 Phase 1 done)
- `docs/design/compiler/` 可加 Compilation 节（保留作为后续 backlog；本 spec
  Compilation 是 thin wrapper，没新设计决策）

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Pipeline/Compilation.cs` | NEW | 不可变 Compilation wrapper |
| `src/compiler/z42.Tests/CompilationTests.cs` | NEW | 单元测试：lazy / cache / WithCompilationUnit immutability |
| `docs/review.md` | MODIFY | F2.1 标 🟡 Phase 1 done |

只读引用：
- `src/compiler/z42.Pipeline/PipelineCore.cs` — `CheckOnly` 委托目标
- `src/compiler/z42.Semantics/TypeCheck/SemanticModel.cs` — 输出类型
- `src/compiler/z42.Pipeline/DependencyIndex.cs` — 构造参数
- `src/compiler/z42.Core/Diagnostics/DiagnosticBag.cs` — 诊断容器

## 设计要点

### Lazy + thread-safe

`Model` / `Diagnostics` 用 `Lazy<T>(LazyThreadSafetyMode.PublicationOnly)`，
第一次访问触发 typecheck，后续访问命中 cache。Phase 1 不需要真正并发安全
（caller 单线程使用），但 PublicationOnly 是几乎零代价的稳健默认。

### With-* immutable update

`WithCompilationUnit(newCu)` 返回**新**的 `Compilation`，原实例不变。Roslyn 风格。
返回的 Compilation 有自己的 Lazy，与原 Compilation 完全独立 —— 不共享缓存
（缓存绑定到具体 CU，不同 CU 自然有不同绑定结果）。

### 为什么不修改 PipelineCore

PipelineCore 是 procedural pipeline。Compilation 是 caller-facing wrapper。
两层分离：Pipeline 做"如何编译"，Compilation 做"编译输入是不可变的"。
保持 PipelineCore 当前 callers (PackageCompiler, SingleFileCompiler) 路径
不变。

### 不引入 SyntaxTree

review.md 草稿里 `Compilation.SyntaxTrees: IReadOnlyList<SyntaxTree>` 假设
z42 有 SyntaxTree 类型。当前 z42 没有 —— `CompilationUnit` 是直接的 AST
顶层。引入 SyntaxTree wrapper 是 Phase 2 工作。Phase 1 直接持 CompilationUnit。

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 NEW `z42.Pipeline/Compilation.cs`
- [x] 1.2 NEW `z42.Tests/CompilationTests.cs` (5 测试: Create / Model lazy + cached / Diagnostics typecheck-error / WithCompilationUnit immutable + cache preserved / WithImported)
- [x] 1.3 VERIFY `dotnet test` 1464/1464 全过（filter exclude IncrementalBuildIntegrationTests due to unrelated WIP）
- [x] 1.4 MODIFY `review.md` 标 🟡 Phase 1 done
- [x] 1.5 归档 + commit + push
