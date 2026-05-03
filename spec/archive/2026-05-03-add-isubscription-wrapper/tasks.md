# Tasks: D2b — ISubscription wrapper 体系

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：stdlib（完整流程）
> **依赖**：D2a 已 GREEN；前置三 spec 已 GREEN：
> - `fix-nested-generic-parsing`（commit e8e71cb）
> - `fix-z42type-structural-equality`（commit cba09aa）
> - `fix-generic-member-substitution`（commit d551aa7，INVESTIGATED）

## 进度概览
- [x] 阶段 1: stdlib ISubscription / SubscriptionRefs（commit 66ca7d4）
- [x] 阶段 2: MulticastAction 双 vec 改造（解封后落地）
- [x] 阶段 3: 测试
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: stdlib wrapper 类
- [x] 1.1 NEW `src/libraries/z42.core/src/ISubscription.z42` —— interface 定义
- [x] 1.2 NEW `src/libraries/z42.core/src/SubscriptionRefs.z42` —— ModeFlags + StrongRef + OnceRef + CompositeRef
- [x] 1.3 ~~`Action<T>.AsOnce()` impl 扩展~~ —— 推迟（D2 路线 D-2 deferred；CompositeRef.WithMode 已可达成"once"）
- [x] 1.4 ~~验证 `TD?` 返回类型~~ —— 用 `TD Get()` + 调用方 IsAlive 守卫的方案（已落地）

## 阶段 2: MulticastAction 双 vec（解封后落地）
- [x] 2.1 拆分 strong + advanced 两组数组（capacity / count / alive 各自维护）
- [x] 2.2 `SubscribeAdvanced(ISubscription<Action<T>>)` 重载进 advanced 路径
- [x] 2.3 `RemoveAt(int idx, bool isAdvanced)` 双通道路由；MulticastSubscription token 加 isAdvanced flag
- [x] 2.4 Invoke 双 loop：strong fast（裸调用）+ advanced slow（IsAlive → Get → 调用 → OnInvoked）
- [x] 2.5 Count() 累加 strong alive + advanced (alive && IsAlive)
- [x] 2.6 GrowStrong / GrowAdvanced 各自 grow

## 阶段 3: 测试
- [x] 3.1 ~~NEW `SubscriptionRefsTests.cs`~~ —— 跳过：wrappers 是 stdlib z42 类，C# 单元测试不直接消费；golden 端到端覆盖
- [x] 3.2 NEW `src/runtime/tests/golden/run/multicast_subscription_refs/source.z42` —— strong + OnceRef + CompositeRef once / persist 模式 + Count + Dispose 双通道路由
- [x] 3.3 NEW `expected_output.txt`
- [x] 3.4 ~~NEW `examples/multicast_subscription.z42`~~ —— 跳过：与 D2a 一致（D2a 也只 golden 无 examples/），golden source.z42 已是完整 demo
- [x] 3.5 `./scripts/regen-golden-tests.sh` 118 ok
- [x] 3.6 D2a `multicast_action_basic` golden 仍 GREEN（strong fast path 行为保持）

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet build` ✅
- [x] 4.2 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 4.3 `dotnet test` 952/952 ✅（基线 951 + 1 multicast_subscription_refs golden 自发现）
- [x] 4.4 `./scripts/test-vm.sh` 232/232 ✅（基线 230 + interp/jit 各 1）
- [x] 4.5 IncrementalBuildIntegrationTests 文件计数 36 → 38（已在 fix-nested-generic-parsing 修过）
- [x] 4.6 spec scenarios 逐条核对
- [x] 4.7 文档同步：
    - `docs/design/delegates-events.md` 顶部状态 D2b 落地 + WeakRef 延后说明
    - `docs/roadmap.md` 加 2026-05-03 add-isubscription-wrapper 行
- [x] 4.8 移动 `spec/changes/add-isubscription-wrapper/` → `spec/archive/2026-05-03-add-isubscription-wrapper/`
- [x] 4.9 commit + push

## 备注
- WeakRef 延后到 follow-up `expose-weak-ref-builtin`（docs/deferred-features.md D-1）
- `event` 关键字（D2c）/ MulticastFunc + 异常聚合（D2d）独立批次
- ISubscription chain `.AsOnce()` 跨 generic interface impl 扩展在 v1 不实现；
  CompositeRef.WithMode 累加 flag 替代（docs/deferred-features.md D-2）
- 实施流程：D2b 阶段 1 上半部分 commit 66ca7d4（wrappers landed, blocked），
  generic 系统批量 3 spec（commits e8e71cb / cba09aa / d551aa7），D2b 解封继续阶段 2-4
