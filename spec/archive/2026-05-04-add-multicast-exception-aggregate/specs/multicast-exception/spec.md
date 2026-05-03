# Spec: MulticastException 异常聚合（Action 路径）

## ADDED Requirements

### Requirement: `Std.AggregateException` 基类

#### Scenario: 创建并查询
- **WHEN** `var ae = new AggregateException("X failed", innerExceptions);`
- **THEN** `ae.Message == "X failed"`，`ae.InnerExceptions.Length == innerExceptions.Length`，`ae instanceof Exception` true

### Requirement: `Std.MulticastException` 子类

#### Scenario: 携带 Failures 索引 + Exception 数组
- **WHEN** `new MulticastException(failures, indices, totalHandlers)` 其中 failures.Length == indices.Length
- **THEN** `ex.Failures == failures`，`ex.FailureIndices == indices`，`ex.TotalHandlers == totalHandlers`，`ex.SuccessCount == totalHandlers - failures.Length`

### Requirement: `MulticastAction.Invoke(continueOnException=true)` 聚合

#### Scenario: 0 异常不抛
- **WHEN** 所有 handler 正常完成，调用 `bus.Invoke(arg, true)`
- **THEN** 不抛异常

#### Scenario: 1 异常聚合抛 MulticastException
- **WHEN** 3 handler 中第 2 个抛 `Exception("oops")`，调用 `bus.Invoke(arg, true)`
- **THEN** 抛 `MulticastException`，`ex.Failures.Length == 1`，`ex.FailureIndices[0] == 1`（基于 0），`ex.TotalHandlers == 3`，`ex.SuccessCount == 2`
- **AND** 其他 2 个 handler 全部被调用（不打断）

#### Scenario: 多异常聚合
- **WHEN** 3 handler 全部抛
- **THEN** 抛 `MulticastException`，`ex.Failures.Length == 3`，`ex.SuccessCount == 0`

#### Scenario: continueOnException=false 保持 fail-fast
- **WHEN** 第一个 handler 抛，参数 false（默认）
- **THEN** 抛该原异常（不包装），后续 handler 不调用（与现行 D2a/b 行为一致）

### Requirement: 同 advanced 通道一致

#### Scenario: advanced wrapper handler 失败也聚合
- **WHEN** 包 OnceRef 的 handler 抛异常 + continueOnException=true
- **THEN** 也累入 Failures，OnInvoked 不调（异常发生在 Get()(arg) 调用时）

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] VM interp（不动 —— stdlib 纯 z42 实现，复用既有 try/catch + throw 路径）
