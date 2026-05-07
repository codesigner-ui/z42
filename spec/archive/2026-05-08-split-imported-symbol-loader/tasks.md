# Tasks: split-imported-symbol-loader

> 状态：🟢 已完成 | 创建：2026-05-08 | 完成：2026-05-08
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 1 §1.1 P0 残留（4 个超 500 LOC 文件之一，3/4）

**变更说明**: 把 `src/compiler/z42.Semantics/TypeCheck/ImportedSymbolLoader.cs` (730 LOC) 改为 `public static partial class`，按 review.md 描述的 Phase 分组拆为 1 主文件 + 5 个 partial 子文件。

**原因**: 单文件 730 LOC 超 500 硬限。Load 主入口 + 三段 Phase helper + 共享 type resolver 天然解耦，按主题拆有助导航。零行为变化。

## 验证报告

### 编译状态
- ✅ `dotnet build src/compiler/z42.slnx`: 0 Warning / 0 Error

### 测试结果
- ✅ `dotnet test`: **1104/1104**
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**

### 拆分结果
| 文件 | LOC | 涵盖 |
|---|---|---|
| ImportedSymbolLoader.cs (主) | 167 | doc + class + ParseVisibility + Empty + Combine |
| ImportedSymbolLoader.Load.cs | 236 | 两个 Load overload + 主 Phase 1-4 编排 |
| ImportedSymbolLoader.TypeResolver.cs | 159 | RebuildFuncType / ResolveTypeName / FindGenericOpenLt / SplitGenericArgs |
| ImportedSymbolLoader.Phase2.cs | 95 | FillClassMembersInPlace / FillInterfaceMembersInPlace |
| ImportedSymbolLoader.Phase3.cs | 78 | MergeImpls / SplitFqName |
| ImportedSymbolLoader.Phase1.cs | 44 | BuildClassSkeleton / BuildInterfaceSkeleton |

全部 ≤ 250 LOC（最大 Load.cs 236）。

### 结论：✅ 全绿，可归档
