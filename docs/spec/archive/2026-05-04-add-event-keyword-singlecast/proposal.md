# Proposal: D-7 — 单播 event 关键字（Action/Func/Predicate）

## Why

D2c-多播 ship 多播 event；Spec 2b 设计的单播 event（`event Action<T>` /
`event Func<T,R>` / `event Predicate<T>`）当前 parser 报 "single-cast event
not yet supported"。本 spec 解锁 —— 让 Cocoa-style 回调属性可用。

## What Changes

- **Parser `SynthesizeClassEvent` / `SynthesizeInterfaceEvent`** 类型校验扩展：
  - 接受 `Action<T>` / `Action<T1,T2>` ... / `Func<...>` / `Predicate<T>` 单播类型
  - 单播路径合成：
    - 字段类型 → `T?`（OptionType wrap）
    - 字段 init → null（无 `NewExpr`，OptionType 默认 null）
    - `add_<X>(handler) → void` body：
      - `if (this.X != null) throw new InvalidOperationException("single-cast event already bound");`
      - `this.X = handler;`
    - `remove_<X>(handler) → void` body：
      - `if (DelegateOps.ReferenceEquals(this.X, handler)) this.X = null;`
- 多播路径不变（Action / Func / Predicate 改进保持兼容多播 vs 单播分发）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | `SynthesizeClassEvent` / `SynthesizeInterfaceEvent` 加单播路径分支 |
| `src/compiler/z42.Tests/EventKeywordTests.cs` | MODIFY | 加单播 event 测试 |
| `src/runtime/tests/golden/run/event_keyword_singlecast/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/event_keyword_singlecast/expected_output.txt` | NEW | 预期输出 |

**只读引用**：

- D2c archive — `SynthesizeClassEvent` 多播模式参考
- `src/libraries/z42.core/src/Exceptions/InvalidOperationException.z42` —— 单播 throw 用

## Out of Scope

- `add_<X>` 返回 IDisposable（v1 返回 void；用户用 `-=` 移除）
- 严格 access control（外部 invoke / 直接 set 报错）—— 留独立 spec
- D-1b WeakRef wrapper

## Open Questions

- [ ] 多 arity Action / Func（Action<T1,T2,T3> / Func<T1,T2,R>）—— 当前 stdlib 仅 0-4 arity Action / 0-4 arity Func，handler 类型直接用字段类型即可
