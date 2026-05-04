# Tasks: WeakRef ISubscription wrapper（D-1b）

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04
>
> **实施期间发现两层 stop-and-ask**：
> 1. **Phase 1 阻塞**：z42 instance method group conversion `obj.Method` 完全未实现（D1b 只覆盖 free fn）。需要新 compiler 路径：`EmitBoundMember` emit thunk + `MkClos([recv])`。User 裁决"实现 D-1b"包含修复此前置。
> 2. **Phase 3 阻塞**：原 design 让 WeakRef 强持 handler，会反向锁住 receiver（Closure 强持 env=[recv]），weak 失效。需新增 2 个 corelib builtin (`__delegate_fn_name` / `__make_closure`) + 重写 WeakRef 为 (WeakHandle, fnName) 模式，Get 时重建 Closure。
>
> 实际产出比原 spec 大：原计划 1 个 builtin + WeakRef 类，实际 3 个 builtin + 1 个 thunk emit 机制 + WeakRef 类 + CompositeRef 重写。但通过 stop-and-ask 与 User 同步推进，scope 扩张已知情。

## 进度概览
- [ ] 阶段 1: 验证 method group conversion 形态（gating check）
- [ ] 阶段 2: VM builtin `__delegate_target`
- [ ] 阶段 3: stdlib `Std.DelegateOps.GetTarget` extern
- [ ] 阶段 4: stdlib `Std.WeakRef<TD>` 类
- [ ] 阶段 5: CompositeRef.Mode.Weak 接入
- [ ] 阶段 6: 测试 + 文档

## 阶段 1: 验证 method group conversion 形态（gating check）
- [ ] 1.1 写一个临时小 z42 程序：`class Foo { void Bar() {} }; var f = new Foo(); Action h = f.Bar; print(h);` 用 `--emit zbc` 看 emit 的指令是 `MakeClosure` 还是 `MakeFuncRef`
- [ ] 1.2 若是 Closure with env=[obj] → 继续阶段 2；若是 FuncRef → **停下报告 User**，开新 spec 调整 method group conversion，本 spec 暂停（Decision 4）

## 阶段 2: VM builtin `__delegate_target`
- [ ] 2.1 `src/runtime/src/native/...` 加 `delegate_target(args) -> Result<Value>` 实现（按 Decision 1 模板）
- [ ] 2.2 在 builtin registry 注册名 `__delegate_target` → 函数指针
- [ ] 2.3 单元测试 `delegate_target_tests.rs` 覆盖 Closure with object env / 无 env / StackClosure / FuncRef / null

## 阶段 3: stdlib `Std.DelegateOps.GetTarget` extern
- [ ] 3.1 `src/libraries/z42.core/src/Delegates.z42`（或 DelegateOps.z42）加 `[Native("__delegate_target")] public static extern object? GetTarget(object delegate);`
- [ ] 3.2 跑 build-stdlib.sh 验证编译通过

## 阶段 4: stdlib `Std.WeakRef<TD>` 类
- [ ] 4.1 `src/libraries/z42.core/src/SubscriptionRefs.z42` 加 `WeakRef<TD>` 类（按 design Implementation Notes 模板）
- [ ] 4.2 实现 ISubscription<TD> 四个方法：Get / IsAlive / OnInvoked + ctor
- [ ] 4.3 注释清楚 isDegraded 退化逻辑

## 阶段 5: CompositeRef.Mode.Weak 接入
- [ ] 5.1 `SubscriptionRefs.z42` `CompositeRef<TD>` 加 `_weakHandle: WeakHandle?` 字段
- [ ] 5.2 ctor 检查 `(modes & Weak) != 0` → 提取 target → MakeWeak
- [ ] 5.3 Get / IsAlive 路径加 weak 分支（与 Once 正交）
- [ ] 5.4 删掉 line 91 的 `// Weak / 其他 mode 在 D2b-followup 启用` 占位注释

## 阶段 6: 测试 + 文档
- [ ] 6.1 NEW golden `src/libraries/z42.core/tests/golden/weak_subscription_lapsed/source.z42` + expected
- [ ] 6.2 NEW golden `src/libraries/z42.core/tests/golden/composite_ref_weak_mode/source.z42` + expected
- [ ] 6.3 `docs/design/delegates-events.md` §5.2 / §5.3 / status 行更新 D-1b 落地；line 191 退化策略说明
- [ ] 6.4 `docs/design/vm-architecture.md` `__delegate_target` builtin 加入清单
- [ ] 6.5 `docs/deferred.md` 移除 D-1b 条目

## 阶段 7: 验证
- [ ] 7.1 `dotnet build src/compiler/z42.slnx && cargo build --manifest-path src/runtime/Cargo.toml` —— 无编译错误
- [ ] 7.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 全绿
- [ ] 7.3 `./scripts/test-vm.sh` —— 全绿（含两个新 golden + 现有 weak_ref_basic 不回归）
- [ ] 7.4 spec scenarios 6 个场景逐条对应 ✅
- [ ] 7.5 文档同步：`docs/design/delegates-events.md`、`docs/design/vm-architecture.md`、`docs/deferred.md`

## 备注
- 阶段 1 是 gating check，发现 method group 形态不对必须停下来报告
- `__delegate_target` 退化策略 lenient（返回 null）（Decision 2）
- CompositeRef.Mode.Weak 内嵌 WeakHandle 而非 WeakRef 实例（Decision 3）
