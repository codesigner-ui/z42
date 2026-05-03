# Tasks: D2d-2 — MulticastException 聚合（Action 路径）

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-04 | 类型：stdlib
> **依赖**：D2a + D2d-1 GREEN

## 进度概览
- [x] 阶段 1: stdlib AggregateException + MulticastException
- [x] 阶段 2: MulticastAction.Invoke 聚合实现 + 三类 overload 拆分（z42 默认参数 bug workaround）
- [x] 阶段 3: 测试
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: stdlib
- [x] 1.1 NEW `Exceptions/AggregateException.z42` 继承 Exception，加 InnerExceptions
- [x] 1.2 NEW `Exceptions/MulticastException.z42` 继承 AggregateException，加 Failures + FailureIndices + TotalHandlers + SuccessCount()

## 阶段 2: MulticastAction.Invoke 聚合 + overload 拆分
- [x] 2.1 `MulticastAction.Invoke` 拆 `Invoke(T arg)` + `Invoke(T arg, bool continueOnException)` 两 overload —— **z42 默认参数 bool 读取报 Null bug 的 workaround**
- [x] 2.2 2-arg 路径加 try/catch 累积 Failures + FailureIndices + TotalHandlers，最终抛 MulticastException
- [x] 2.3 `MulticastFunc.Invoke` / `MulticastPredicate.Invoke` 同款拆 overload（z42 method overload 影响跨类 dispatch，必须同步）

## 阶段 3: 测试
- [x] 3.1 NEW `multicast_exception_aggregate/source.z42` 端到端：0 异常 / 1 异常 / 多异常聚合 / fail-fast 保持原行为
- [x] 3.2 NEW `expected_output.txt`
- [x] 3.3 `./scripts/regen-golden-tests.sh` 123 ok
- [x] 3.4 既有 golden 全 GREEN（D2a/b/c/D2c-多播/D2c-interface/D-5/D2d-1）

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet build` ✅
- [x] 4.2 `dotnet test` 967/967 ✅（基线 966 + 1 multicast_exception_aggregate golden 自发现）
- [x] 4.3 `./scripts/test-vm.sh` 242/242 ✅（基线 240 + interp/jit 各 1）
- [x] 4.4 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 4.5 IncrementalBuildIntegrationTests 41 → 43
- [x] 4.6 spec scenarios 逐条核对
- [x] 4.7 文档同步：
  - `docs/design/delegates-events.md` 顶部状态加 D2d-2-Action
  - `docs/roadmap.md` 加 2026-05-04 行
  - `docs/deferred-features.md` D-8 → D-8b（仅 Func/Predicate 留 follow-up）+ NEW D-9（z42 默认参数 bug 跟踪）
- [x] 4.8 移动 `spec/changes/add-multicast-exception-aggregate/` → `spec/archive/2026-05-04-add-multicast-exception-aggregate/`
- [x] 4.9 commit + push

## 备注
- 实施期间发现 z42 默认参数 bool 读取报 Null bug → 拆 overload workaround；
  bug 跟踪 D-9（独立修复）
- Func/Predicate 异常聚合 + MulticastException<R> 泛型留 D-8b follow-up
- WeakRef 留 D-1 batch
- 单播 event 留 add-event-keyword-singlecast
