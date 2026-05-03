# Proposal: D2c — interface event default 实现 (I10)

## Why

[delegates-events.md §6.5 I10](docs/design/delegates-events.md#65-改进项)：interface 中声明的 `event` 应自动合成 add/remove signature 要求，implementer 在 class body 写 `event ...` 即满足契约 —— 用户无需手写 add_X / remove_X。

D2c-多播（Spec 2）已实现 class 端合成 add_X / remove_X 方法体；本 spec 加 interface 端的"声明侧"对偶。

## What Changes

- **Parser**：interface body 接受 `event` modifier 在 field-like 声明上
  - `event MulticastAction<T> X;` 在 interface 内合成 2 个 MethodSignature：
    - `add_X(Action<T>): IDisposable`（instance abstract，无 body）
    - `remove_X(Action<T>)`（instance abstract，无 body）
  - 单播 event（`event Action<T> Y` 等）：报与 D2c-多播 一致的 "single-cast event not yet supported"
- **TypeChecker**：interface 实现一致性检查复用既有路径 —— class 合成的 add_X/remove_X 方法签名匹配 interface 声明的 add_X/remove_X signature 即满足契约
- **stdlib**：D2d 后扩 MulticastFunc / MulticastPredicate 时同步放宽 interface 端校验

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | `ParseInterfaceDecl` 检测 `event` modifier；调用新 helper 合成 MethodSignatures |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | 加 `SynthesizeInterfaceEvent(typeExpr, name, span)` 助手 |
| `src/compiler/z42.Tests/EventKeywordTests.cs` | MODIFY | 加 interface event 测试 |
| `src/runtime/tests/golden/run/interface_event/source.z42` | NEW | 端到端 golden（interface 声明 + class 实现 + `+=`/`-=` 通过 interface 接收对象） |
| `src/runtime/tests/golden/run/interface_event/expected_output.txt` | NEW | 预期输出 |

**只读引用**：

- `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` — `SynthesizeInterfaceAutoProp` 模式参考
- `src/libraries/z42.core/src/MulticastAction.z42` — 多播 event 委托对象
- D2c-多播 archive — `SynthesizeClassEvent` 对偶

## Out of Scope

- 单播 event（→ Spec 2b）
- D2d MulticastFunc/Predicate event（D2d 后再放宽校验）
- interface 中 event field 提供 default body（z42 instance default interface methods 暂未支持）

## Open Questions

- 无（此 spec 范围窄）
