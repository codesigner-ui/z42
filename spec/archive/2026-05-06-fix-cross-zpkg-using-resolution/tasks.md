# Tasks: fix-cross-zpkg-using-resolution

> 状态：🟢 已完成 | 创建：2026-05-06 | 类型：fix（minimal mode）

**变更说明**：修复 `using <ns>;` 在 namespace 仅由 impl-only 包提供时被误判为 E0602 UnresolvedUsing 的回归。

**原因**：`25a8505 strict-using-resolution`（2026-04-28）引入的 E0602 检查使用 `imported.ClassNamespaces.Values` 作为 resolved namespaces 的真相源。但该字段只记录有 class 声明的命名空间。

L3-Impl2 的 cross-zpkg `impl Trait for Type` 场景下，impl-providing 包（例如 cross-zpkg test 中的 `demo.greeter` zpkg）只有 impl 块、没有 class，因此 `Demo.Greeter` 命名空间不出现在 `ClassNamespaces` 中——即使 zpkg 的 NSPC section 已正确声明且 TsigCache 已正确激活该包，TypeChecker 仍报 `E0602 using \`Demo.Greeter\`: no loaded package provides this namespace`。

回归区间通过 `git bisect` 锁定为 `25a8505`（前一 commit `c46a78d` PASS）。

**根因**（feedback_problem_first_then_defer / 设计完整性原则）：E0602 应该问的是"激活的包里有哪些 namespace"而不是"加载的类里有哪些 namespace"。这两个集合在大部分场景重合（每个包都有 class），但 impl-only 包是反例——必须从源头（包激活集）算 resolved namespaces，不能在消费侧拼凑。

**文档影响**：[docs/deferred.md](docs/deferred.md) `fix-cross-zpkg-using-resolution` 条目移除（已落地）。

---

## 修复方案

1. `ImportedSymbols` 增字段 `HashSet<string>? ResolvedNamespaces`
2. `ImportedSymbolLoader.Load` 在主 scan 循环中收集 `mod.Namespace` for every `pkg ∈ allowedPkgs`
3. `Combine()` 合并新字段
4. `TypeChecker.EmitImportDiagnostics` E0602 检查改用 `ResolvedNamespaces ∪ ClassNamespaces.Values ∪ {ownNs}`（保留 `ClassNamespaces` 的 fallback 让旧 2-arg `Load(modules, usings)` 路径不破）

## Tasks

- [x] 1.1 `ImportedSymbolLoader.cs`：增 `ResolvedNamespaces` 字段 + Load 收集 + Combine 合并
- [x] 1.2 `TypeChecker.cs::EmitImportDiagnostics`：E0602 用三源 union
- [x] 1.3 `UsingResolutionTests.cs`：补回归测试 `TypeChecker_NoError_When_UsedNamespaceHasOnlyImpls`（mock impl-only module + assert ResolvedNamespaces 包含 + 无 E0602）
- [x] 1.4 `docs/deferred.md`：删除 `fix-cross-zpkg-using-resolution` 条目（已落地）
- [x] 1.5 验证：`dotnet build` + `dotnet test`（1054/1054）+ `./scripts/test-vm.sh`（interp 134/134）+ `./scripts/test-cross-zpkg.sh`（1/1）+ `cargo test`（261/261）
- [x] 1.6 commit + push + 归档
