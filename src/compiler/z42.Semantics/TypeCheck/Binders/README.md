# z42.Semantics.TypeCheck.Binders

## 职责

Roslyn 风格的 `Binder` hierarchy 抽象 —— 把"按 scope 的 symbol lookup"从
单体 `TypeEnv` 拆为多态子类链。

**当前状态（review.md F2.4 Phase 1, 2026-06-03）**：scaffold，**未接入
TypeChecker**。完整设计 + Phase 2-N migration 路径见
[`docs/design/compiler/binder-hierarchy.md`](../../../../../docs/design/compiler/binder-hierarchy.md)。

## 核心文件

| 文件 | 职责 |
|------|------|
| `Binder.cs` | abstract base — `Next` 链 + `LookupSymbol` virtual 默认 forward |
| `GlobalScopeBinder.cs` | 根节点 — module 顶层 functions / classes |
| `InMethodBinder.cs` | 方法 scope — 参数 + this context |
| `InBlockBinder.cs` | block scope — local 变量 + shadowing |

## 入口点

未来 TypeChecker 重构时，每次进入新 scope 创建对应 Binder 子类实例，
push 到 chain。LookupSymbol 自动沿链 forward。

## 依赖关系

→ `z42.Semantics.Symbols.ISymbol` —— 查询返回类型
（其余依赖 Phase 2+ 接 TypeChecker 时引入）

## 不变量

1. `Next` 一旦构造不可变（chain 是 immutable linked list）
2. 子类 override `LookupSymbol` 必须先查自己再 `base.LookupSymbol(name)`
   forward，否则破坏 shadowing 语义
3. Phase 1 stub 子类用 `Dictionary<string, ISymbol>` 持自己 scope 的 slot；
   Phase 2+ 可换成 SymbolTable / DependencyIndex 等更复杂的查询源
