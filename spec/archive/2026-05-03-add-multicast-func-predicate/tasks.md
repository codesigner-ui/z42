# Tasks: D2d-1 — MulticastFunc + MulticastPredicate

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：stdlib + lang/parser
> **依赖**：D2a + D2b + D-5 + D2c-多播 + D2c-interface GREEN

## 进度概览
- [x] 阶段 1: stdlib MulticastFunc.z42 + MulticastPredicate.z42
- [x] 阶段 2: parser 校验放宽（class + interface）
- [x] 阶段 3: 测试
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: stdlib
- [x] 1.1 NEW `MulticastFunc.z42` 双 vec + Subscribe/SubscribeAdvanced/Unsubscribe/Invoke 返回 R[]
- [x] 1.2 NEW `MulticastPredicate.z42` 同上 + All / Any 短路

## 阶段 2: parser 放宽
- [x] 2.1 `MulticastTypeNames` HashSet + `HandlerTypeName` switch helper
- [x] 2.2 `SynthesizeClassEvent` 校验放宽 + handler 类型映射
- [x] 2.3 `SynthesizeInterfaceEvent` 同款放宽

## 阶段 3: 测试
- [x] 3.1 `EventKeywordTests` +3：MulticastFunc / Predicate event field + interface event MulticastFunc
- [x] 3.2 NEW `multicast_func_predicate/source.z42` 端到端
- [x] 3.3 NEW `expected_output.txt`
- [x] 3.4 `./scripts/regen-golden-tests.sh` 122 ok
- [x] 3.5 D2a/b/c 既有 golden 仍 GREEN

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet build` ✅
- [x] 4.2 `dotnet test` 966/966 ✅（基线 962 + 3 EventKeywordTests + 1 golden 自发现）
- [x] 4.3 `./scripts/test-vm.sh` 240/240 ✅（基线 238 + interp/jit 各 1）
- [x] 4.4 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 4.5 IncrementalBuildIntegrationTests 39 → 41
- [x] 4.6 spec scenarios 逐条核对
- [x] 4.7 文档同步：
  - `docs/design/delegates-events.md` 顶部状态加 D2d-1
  - `docs/roadmap.md` 加 2026-05-03 行
- [x] 4.8 移动 `spec/changes/add-multicast-func-predicate/` → `spec/archive/2026-05-03-add-multicast-func-predicate/`
- [x] 4.9 commit + push

## 备注
- continueOnException=true 异常聚合留 D2d-2
- 各 multicast 类各自合成自己的 IDisposable token 类（`MulticastSubscription` / `MulticastFuncSubscription` / `MulticastPredicateSubscription`）—— z42 不支持跨类型 generic 共享
- WeakRef 留 D-1 batch
- 单播 event 留 add-event-keyword-singlecast
