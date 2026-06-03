# Tasks: F2.4 Phase 1 — Binder hierarchy scaffold + migration design doc

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor scaffold + design doc
> 来源：[`docs/review.md`](../../../review.md) Part 6 F2.4

## 变更说明

review.md F2.4 完整版（14-21 天）是把 TypeChecker 现有的 `TypeEnv`（单一 sealed
class，~200 LOC，dictionary-based lookups）拆成 Roslyn 风格的 `Binder` 多态
hierarchy（每种 scope 一个 Binder 子类，沿链 forward lookup）。这是
TypeChecker 的核心架构重构，远超一个 session 容量。

Phase 1 切片：

1. **NEW `Binder` abstract base + 3 个 stub 子类** —— GlobalScopeBinder /
   InMethodBinder / InBlockBinder，提供链式 LookupSymbol 默认实现。**当前不
   接入 TypeChecker**——纯 scaffold，证明 chain dispatch 可用。
2. **NEW design doc `docs/design/compiler/binder-hierarchy.md`** —— 详细的
   migration 路径：哪些 TypeEnv 字段 / 方法对应哪种 Binder，Phase 2-N 怎么
   渐进迁移 TypeChecker。
3. **NEW BinderTests.cs** —— 链式 lookup 在 stub binders 上工作。

下一个 spec（Phase 2）把 TypeChecker 的一个具体 binding site（候选：
`BindClassMethods`）迁到 InMethodBinder，验证抽象有用。

## 原因

不迁 consumer 的 Phase 1 看似 cargo-cult，但 Roslyn 当年 Binder 落地路径也
是先建抽象 → 后迁移 sites。design doc 是 Phase 1 真正最重要的 deliverable：
**让未来 agents 不必从零设计就能继续 migration**。代码 scaffold 是 design doc
的"可执行示例"。

## 文档影响

- NEW `docs/design/compiler/binder-hierarchy.md` —— 长期规范
- `docs/review.md` F2.4 状态 🟡 Phase 1 done (3 个 Binder + design doc)

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Semantics/TypeCheck/Binders/Binder.cs` | NEW | abstract base + LookupSymbol virtual + Next link |
| `src/compiler/z42.Semantics/TypeCheck/Binders/GlobalScopeBinder.cs` | NEW | global functions / classes namespace |
| `src/compiler/z42.Semantics/TypeCheck/Binders/InMethodBinder.cs` | NEW | method params + return type context |
| `src/compiler/z42.Semantics/TypeCheck/Binders/InBlockBinder.cs` | NEW | block-scope locals |
| `src/compiler/z42.Semantics/TypeCheck/Binders/README.md` | NEW | quick orientation, points to design doc |
| `docs/design/compiler/binder-hierarchy.md` | NEW | long-form design doc + migration plan |
| `src/compiler/z42.Tests/BinderTests.cs` | NEW | scope chain lookup tests |
| `docs/review.md` | MODIFY | F2.4 status 🟡 Phase 1 done |

只读引用：
- `src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs` — 现有 monolithic env（被
  design doc 引用作迁移源）
- `src/compiler/z42.Semantics/Symbols/ISymbol.cs` — Binder.LookupSymbol 返回类型

## 设计要点（详见 design doc）

### Binder base contract

```csharp
public abstract class Binder {
    public Binder? Next { get; }                  // parent scope or null
    
    /// Lookup an ISymbol by name. Default impl forwards to Next; concrete
    /// subclasses override to consult their own scope before forwarding.
    public virtual ISymbol? LookupSymbol(string name) => Next?.LookupSymbol(name);
    
    /// Convenience: produce a child binder of given subtype. Phase 1
    /// stub; Phase 3 will add specific factory methods on each subclass.
    public T PushScope<T>(Func<Binder, T> factory) where T: Binder => factory(this);
}
```

### Stub 子类（Phase 1）

每个子类持自己的 `Dictionary<string, ISymbol>` slot table。覆盖
`LookupSymbol` 先查自己再 forward。Phase 2-N 会替换 Dictionary 为 Roslyn 风格
"按需 bind" 的更复杂查询，但 Phase 1 接口稳定。

### 不接 TypeChecker 的理由

TypeChecker 当前 ~2000 LOC，用 TypeEnv 跨多个 partial files。把 lookup 改走
Binder 是大手术（每个 TypeEnv.Lookup* 调用都要替换 + InClass / InCatch /
InLambda 等 scope 边界都要确立）。Phase 1 验证抽象，Phase 2 迁第一个 site
（提议：`BindClassMethods`，它有明确的 method-scope 边界）。

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 NEW `Binders/Binder.cs` abstract base + Next 链式
- [x] 1.2 NEW `Binders/GlobalScopeBinder.cs` / `InMethodBinder.cs` / `InBlockBinder.cs` (stub)
- [x] 1.3 NEW `Binders/README.md`
- [x] 1.4 NEW `docs/design/compiler/binder-hierarchy.md` (Roslyn 对照 + Phase 2-5 migration plan)
- [x] 1.5 NEW `z42.Tests/BinderTests.cs` 7 测试 (own scope / unknown / fall-through / shadowing / duplicate-decl / 3-level chain / parameter scope leak)
- [x] 1.6 VERIFY `dotnet test` 1474/1474 全过
- [x] 1.7 MODIFY `review.md` 标 🟡 Phase 1 done
- [x] 1.8 归档 + commit + push
