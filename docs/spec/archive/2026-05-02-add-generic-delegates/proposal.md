# Proposal: D1c — stdlib `Action` / `Func` / `Predicate` 真实类型 + 移除 hardcoded desugar

> 这是 `docs/design/delegates-events.md` D1 阶段第三切片，在 D1a/D1b 落地后实施。
> D2（多播 / event / 异常）独立批次。
>
> **2026-05-02 scope 调整**：泛型 delegate + where 约束已移到 D1a 一并实施（user 裁决）。
> D1c 现在的职责限于 **stdlib 真实类型创建** + **移除 SymbolCollector hardcoded desugar**。

## Why

D1a 之后 z42 在编译器层支持 `delegate R Func<T,R>(T arg);` 解析与实例化。但：

- **stdlib 没有真实 `Action` / `Func` / `Predicate` delegate 类型** —— 用户即使 `using Std;` 也找不到这些名字的真实声明
- **`SymbolCollector` 仍有 hardcoded `Action`/`Func` desugar**（line 211, 248-253）—— 这是 D1a 之前的残留，与 D1a 新机制（`SymbolTable.Delegates`）双路径共存
- **`Predicate<T>` 完全缺失**（既无 hardcoded desugar 也无 stdlib）—— 用户写 `Predicate<int> isEven = ...;` 编译失败
- **D2 多播 / event 依赖 stdlib 真实 `Action<T>` / `Func<T,R>` / `Predicate<T>` 类型**（`MulticastAction<T>` 接受 `Action<T>` handler）

## What Changes

- **stdlib（z42.core）**：在 `src/libraries/z42.core/src/Delegates.z42` 新增真实 delegate 声明（`Action<T>` / `Func<T,R>` / `Predicate<T>` × N arity）；先手写 0-4 arity，验证机制后再考虑生成脚本
- **Predicate 补齐**：`Predicate<T>` 显式声明 —— 既覆盖现有 SymbolCollector 缺口，也为 D2 `MulticastPredicate<T>` 提供基础
- **SymbolCollector hardcoded 路径清理**：**删除** line 211, 248-253 的 `"Action"` / `"Func"` 写死 desugar；改由 D1a 的 stdlib delegate 加载路径处理
- **N arity 脚本（暂缓）**：`tools/gen-delegates.z42` 自动生成 16 arity 的脚本属于 z42 自举完成后的工具；D1c v1 手写 0-4 arity 即可 demo 路径，N>4 留 follow-up

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` | MODIFY | 删除 line 211 (`"Action"` 0-arity) + line 248-253 (`"Func"` / `"Action"` GenericType) hardcoded desugar |
| `src/libraries/z42.core/src/Delegates.z42` | NEW | `Action` (0-arity) + `Action<T>` × 1-4 + `Func<T,R>` × 1-4 + `Predicate<T>` |
| `src/libraries/z42.core/z42.toml` 或入口 | MODIFY（如需）| 加 Delegates.z42 入口（如 stdlib 不是 glob 自动收集） |
| `src/compiler/z42.Tests/StdlibDelegateTests.cs` | NEW | stdlib 真实 delegate 解析与实例化 |
| `src/compiler/z42.Tests/PredicateTests.cs` | NEW | Predicate 端到端 |
| `src/runtime/tests/golden/run/delegate_d1c_stdlib/` | NEW | 端到端 golden |
| `examples/delegate_stdlib.z42` | NEW | 演示 |
| `docs/design/delegates-events.md` | MODIFY | D1c 完成标记；§3.4 "脚本生成"改为"0-4 已手写，>4 follow-up" |
| `docs/design/closure.md` §3.2 | MODIFY | 确认与 stdlib 真实 delegate 一致（D1a 已改） |
| `docs/roadmap.md` | MODIFY | 已完成关键 fix 表加一行 |

**只读引用**：
- `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs:211, 248-253` —— 现有 hardcoded `"Action"`/`"Func"` 分支（移除目标）
- `src/libraries/z42.core/src/Object.z42` —— stdlib 类型声明风格参考
- D1a archive —— DelegateInfo / SymbolTable.Delegates 机制

## Out of Scope

- ❌ `tools/gen-delegates.z42` 脚本（依赖 z42 自举完成）—— 目前手写 1-4 arity 足够 demo
- ❌ Action/Func arity 5+ —— 等真实需求 / 脚本工具到位时一次性补
- ❌ delegate where-constraint（`delegate R Func<T, R>(T arg) where T : ...`）—— 与 generics.md 协同
- ❌ delegate 协变 / 逆变（`<in T, out R>`）—— delegates-events.md §12 已明确"推迟到 L3 后期"
- ❌ event 关键字 / 多播（D2）

## Open Questions

- [ ] **stdlib delegate 名字与 SymbolCollector hardcoded desugar 共存阶段**：实施时是否一步清理还是分两步？建议**一步清理**（防止两路径同名互相覆盖造成奇怪行为）
- [ ] **N arity 边界**：1, 2, 3, 4 还是更多？倾向 **0, 1, 2, 3, 4**（覆盖 95% 场景，5+ 用法少）
- [ ] **`Action<>` 的 0-arity 形式**：D1c 是否同时定义 `Action`（无 type param 版本）？delegates-events.md §3.1 line 211 SymbolCollector 现把 `Action`（无 type args）desugar 为 `() -> void` —— 应该保留为 stdlib 真实类型（`public delegate void Action();`）。建议**保留**
- [ ] **泛型 delegate 的 zbc 元数据**：与 generic class / func 一致用现有 type-param 通道（L3-G3a constraint metadata），不新建 SIGS / DELG section？倾向**复用**
