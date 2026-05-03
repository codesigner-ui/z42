# Tasks: D2b — ISubscription wrapper 体系

> 状态：🟡 进行中（2026-05-03 解封） | 创建：2026-05-03 | 类型：stdlib（完整流程）
> **依赖**：D2a 已 GREEN；前置三 spec 已 GREEN：
> - `fix-nested-generic-parsing`（commit e8e71cb）
> - `fix-z42type-structural-equality`（commit cba09aa）
> - `fix-generic-member-substitution`（INVESTIGATED — 实为 Bug 1+3 症状）
>
> **当前已落地（阶段 1 完成，build 绿）**：
> - `src/libraries/z42.core/src/ISubscription.z42`（接口）
> - `src/libraries/z42.core/src/SubscriptionRefs.z42`（StrongRef / OnceRef / CompositeRef）

## 进度概览
- [x] 阶段 1: stdlib ISubscription / SubscriptionRefs（已落地，2026-05-03 早 commit 66ca7d4）
- [ ] 阶段 2: MulticastAction 双 vec 改造（解封继续）
- [ ] 阶段 3: 测试
- [ ] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: stdlib wrapper 类
- [x] 1.1 NEW `src/libraries/z42.core/src/ISubscription.z42` —— interface 定义
- [x] 1.2 NEW `src/libraries/z42.core/src/SubscriptionRefs.z42` —— ModeFlags + StrongRef + OnceRef + CompositeRef
- [ ] 1.3 ~~`Action<T>.AsOnce()` impl 扩展~~ —— 推迟（D2 路线 D-2 deferred；CompositeRef.WithMode 已可达成"once"）
- [ ] 1.4 ~~验证 `TD?` 返回类型~~ —— 用 `TD Get()` + 调用方 IsAlive 守卫的方案（已落地）

## 阶段 2: MulticastAction 双 vec
- [ ] 2.1 修改 `src/libraries/z42.core/src/MulticastAction.z42`：拆分 handlers/alive 为 strong + advanced 两组数组
- [ ] 2.2 加 `Subscribe(ISubscription<Action<T>>)` 重载，进 advanced 路径
- [ ] 2.3 加 advanced 的 RemoveAt 路径（dispose token 共用 MulticastSubscription，靠 idx 区分 advanced/strong slot）—— 用 negative idx 表 advanced，positive 表 strong；或加 bool flag
- [ ] 2.4 修改 Invoke：先 strong fast loop，再 advanced slow loop（TryGet + OnInvoked 路径 + IsAlive latch）
- [ ] 2.5 修改 Count(): sum of strong alive + advanced alive
- [ ] 2.6 修改 Grow(): 双 vec 各自 grow

## 阶段 3: 测试
- [ ] 3.1 NEW `src/compiler/z42.Tests/SubscriptionRefsTests.cs` —— 6 个测试（design Testing Strategy）
- [ ] 3.2 NEW `src/runtime/tests/golden/run/multicast_subscription_refs/source.z42`
- [ ] 3.3 NEW `src/runtime/tests/golden/run/multicast_subscription_refs/expected_output.txt`
- [ ] 3.4 NEW `examples/multicast_subscription.z42` —— 演示
- [ ] 3.5 `./scripts/regen-golden-tests.sh`
- [ ] 3.6 D2a 既有 multicast_action_basic golden 必须继续 GREEN（双 vec 不应破坏 strong fast path 行为）

## 阶段 4: 验证 + 文档 + 归档
- [ ] 4.1 `dotnet build` / `cargo build` 双绿
- [ ] 4.2 `./scripts/build-stdlib.sh` —— stdlib 6/6
- [ ] 4.3 `dotnet test` 全绿（基线 +6）
- [ ] 4.4 `./scripts/test-vm.sh` 全绿（基线 +1×2 modes）
- [ ] 4.5 IncrementalBuildIntegrationTests 文件计数 36 → 38（+ ISubscription.z42 + SubscriptionRefs.z42）；同步更新
- [ ] 4.6 spec scenarios 逐条核对
- [ ] 4.7 文档同步：
    - `docs/design/delegates-events.md` 顶部状态加 D2b；§5 加"WeakRef 延后到 follow-up"
    - `docs/roadmap.md` 加一行
- [ ] 4.8 移动 `spec/changes/add-isubscription-wrapper/` → `spec/archive/2026-05-03-add-isubscription-wrapper/`
- [ ] 4.9 commit + push

## 备注
- WeakRef 延后到 follow-up `expose-weak-ref-builtin`
- `event` 关键字（D2c）/ MulticastFunc + 异常聚合（D2d）独立批次
- ISubscription chain `.AsOnce()` 跨 generic interface impl 扩展在 v1 不实现；
  Action<T> 进入后通过 CompositeRef.WithMode 累加 flag
