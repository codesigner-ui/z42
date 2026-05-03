# Tasks: delegate reference equality + MulticastAction.Unsubscribe

> 状态：🟢 已完成 | 创建：2026-05-03 | 完成：2026-05-03 | 类型：vm/stdlib（完整流程）
> **依赖**：D2a + D2b GREEN（MulticastAction strong 通道存在）
> **解锁**：D2c `-=` desugar；用户 by-handler unsubscribe API

## 进度概览
- [x] 阶段 1: VM corelib `__delegate_eq` builtin
- [x] 阶段 2: stdlib API + MulticastAction.Unsubscribe
- [x] 阶段 3: 测试
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: VM corelib
- [x] 1.1 `src/runtime/src/corelib/object.rs` 加 `pub fn builtin_delegate_eq` —— 3 变体 + null + fallthrough
- [x] 1.2 `src/runtime/src/corelib/mod.rs` 注册 `__delegate_eq`

## 阶段 2: stdlib
- [x] 2.1 NEW `src/libraries/z42.core/src/DelegateOps.z42` —— 静态类 `Std.DelegateOps`，`ReferenceEquals(object, object)` 暴露 builtin（不放 Object —— SymbolTable.cs:111 跳过 Object 跨 CU 导出限制）
- [x] 2.2 `src/libraries/z42.core/src/MulticastAction.z42` 加 `Unsubscribe(Action<T>)` —— linear scan strong[] 调 `DelegateOps.ReferenceEquals` 比较

## 阶段 3: 测试
- [x] 3.1 `src/runtime/src/corelib/tests.rs` 加 11 个 `delegate_eq` 测试 —— 三变体 + 跨变体 + null + 非 delegate
- [x] 3.2 NEW `src/runtime/tests/golden/run/multicast_unsubscribe/source.z42` —— 6 scenarios
- [x] 3.3 NEW `expected_output.txt`
- [x] 3.4 `./scripts/regen-golden-tests.sh` 119 ok
- [x] 3.5 D2a `multicast_action_basic` + D2b `multicast_subscription_refs` 仍 GREEN

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `cargo test --lib` 202/202 ✅（+11 delegate_eq）
- [x] 4.2 `dotnet build` ✅
- [x] 4.3 `dotnet test` 953/953 ✅（基线 952 + 1 multicast_unsubscribe golden 自发现）
- [x] 4.4 `./scripts/test-vm.sh` 234/234 ✅（基线 232 + interp/jit 各 1）
- [x] 4.5 IncrementalBuildIntegrationTests 38 → 39（+ DelegateOps.z42）
- [x] 4.6 spec scenarios 逐条核对
- [x] 4.7 文档同步：
  - `docs/deferred-features.md` D-5 条目移除（已实施）
  - `docs/roadmap.md` 历史表加 2026-05-03 fix-delegate-reference-equality 行
- [x] 4.8 移动 `spec/changes/fix-delegate-reference-equality/` → `spec/archive/2026-05-03-fix-delegate-reference-equality/`
- [x] 4.9 commit + push

## 阶段 5: Scope 中途扩展记录
- 实施时发现 `Object.X` 跨 CU 静态调用受 SymbolTable.cs:111 限制（synthetic stub 跳过 Object 导出）
  → 改放新 `Std.DelegateOps` 静态类（Scope 微扩，已 User 确认）
- 实施时发现 pre-existing 测试 fixture 缺 `func_ref_cache_slots`（D1b 历史遗留）
  → `merge_tests.rs` + `constraint_tests.rs` 各加一行修复（Scope 微扩）

## 备注
- D-5 deferred 项归此 spec 落地
- Closure equality 用 ptr_eq（reference equality）—— 不深比 captured values（Decision 1）
- Unsubscribe 仅 strong 通道；advanced 用 token-based dispose（Decision 3）
- 同 handler 多次 Subscribe → 一次 Unsubscribe 全清（Decision 4，linear scan 不 break）
- DelegateOps.ReferenceEquals 参数用 lowercase `object`（primitive type）—— `Object?` 类要求 IsAssignableTo Z42FuncType→Object 规则不存在
