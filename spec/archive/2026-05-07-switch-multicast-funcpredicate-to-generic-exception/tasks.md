# Tasks: switch-multicast-funcpredicate-to-generic-exception

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07 | 类型：feat(stdlib) + lang(parser) — Scope 扩展后

**变更说明**：`MulticastFunc<T,R>.Invoke(continueOnException=true)` 与 `MulticastPredicate<T>.Invoke(continueOnException=true)` 落地完整 K8 语义 —— 全跑完后失败位置占 `default(R)`（或 `default(bool)`），多失败聚合抛 `MulticastException<R>`（或 `MulticastException<bool>`）。

**原因**：design.md `delegates-events.md` §K8 + `docs/deferred.md` D-8b-1 续作，前置 D-8b-1 (`MulticastException<R>`) + D-8b-3 Phase 2 (`default(T)` runtime 解析) 都已落地，可以收尾 K8 完整语义。

**前置依赖（已解除）**：
- D-8b-1：`Std.MulticastException<R> : MulticastException` 已 land 于 `MulticastException.z42`（2026-05-07）
- D-8b-3 Phase 2：`default(T)` 运行时 type_args 查表已 land（2026-05-07）

**文档影响**：[docs/design/delegates-events.md](../../docs/design/delegates-events.md) §7 实施记录段 + Out-of-Scope 段同步。

**模板参考**：[src/libraries/z42.core/src/Delegates/MulticastAction.z42](../../src/libraries/z42.core/src/Delegates/MulticastAction.z42) `Invoke(continueOnException=true)` 路径（line 130-217）已实现完整聚合 + 抛 `MulticastException`；本 spec 把同样的 try/catch 累积/扩容/聚合/抛 模式复制到 Func/Predicate，加 `result[i] = default(R)` 占位 + `Results` 字段携带的差异。

---

## Tasks

- [x] 1.1 MulticastFunc.z42 `Invoke(continueOnException=true)` 路径：try/catch 每 handler，失败 `result[outIdx] = default(R)`，累积失败索引/异常并行数组，最终抛 `MulticastException<R>(trimE, trimI, total, result)`
- [x] 1.2 MulticastPredicate.z42 同模式；R = bool；失败 `result[outIdx] = default(bool)`；抛 `MulticastException<bool>`
- [x] 1.3 新 golden multicast_func_aggregate（3 handler 中第 2 抛，验证 Results = [7, 0, 49]、Failures.Length=1、TotalHandlers=3）
- [x] 1.4 新 golden multicast_predicate_aggregate（同模式，bool[]）
- [x] 1.5 现有 multicast_func_predicate / multicast_action_basic / multicast_exception_aggregate 等 golden 不破（regression 152 ok 0 fail）
- [x] 1.6 build-stdlib + regen-golden 重建
- [x] 1.7 docs/design/delegates-events.md §7 K8 完整语义实施记录段更新
- [x] 1.8 全套验证：dotnet test 1099/1099、test-vm 295/295（interp 152 + jit 143；2 个新 golden 带 interp_only）、cargo test 全绿
- [x] 1.9 commit + push + 归档

## Scope 扩展记录（实施期）

实施期间发现 `catch (MulticastException<int> e)` parser 不支持（StmtParser.cs 仅接受 identifier），User 裁决 A：本 spec 同时扩 parser。新增改动：

- [x] S.1 [src/compiler/z42.Syntax/Parser/StmtParser.cs](../../src/compiler/z42.Syntax/Parser/StmtParser.cs) catch 子句类型解析后接受 `<...>`，深度计数 `,` → 计算 arity → 拼接 `Name$N` 与 D-8b-0 类注册键对齐
- [x] S.2 TypeChecker 已通过 `BoundCatchClause.ExceptionTypeName` 接受 string，`TryResolveCatchType` 走 `_symbols.Classes.TryGetValue("MulticastException$1")` 直接命中（无需改）

## JIT 跨步（额外）

- [x] J.1 [src/runtime/src/jit/translate.rs](../../src/runtime/src/jit/translate.rs) `Instruction::DefaultOf` 由 bail 改 emit `jit_default_of` helper call
- [x] J.2 [src/runtime/src/jit/helpers_object.rs](../../src/runtime/src/jit/helpers_object.rs) 新 helper `jit_default_of(frame, ctx, dst, param_index) -> u8`，镜像 interp 逻辑
- [x] J.3 jit/translate.rs `HelperIds.default_of` 字段 + `decl!` + `imp!`
- [x] J.4 jit/mod.rs reg! 注册符号
- [x] J.5 stdlib 现在能在 JIT 模式下编译通过（之前 `default(R)` 触发 bail 导致整个 module JIT init 失败）；JIT-allocated 实例 type_args 仍空 → 用户代码内 generic-T `default(T)` 在 JIT 模式仍 graceful Null。两个新 golden 因此带 `interp_only`

---

## 备注

### Out of Scope（本 spec 不做）

- `AggregateException<R>` 泛型化
- `MulticastException<R>.Results` 字段的语义边界（成功值 vs 默认值）的运行时 marker —— 用户通过 `Failures` + `FailureIndices` 反查
- JIT 路径优化（goldens 用 interp_only 标记如必要 —— 取决于 default(T) 是否在 JIT 路径上跑；若是则加标记）

### 验证场景

| 场景 | golden / 测试位置 |
|------|---------|
| `MulticastFunc<int, int>.Invoke(arg, true)` 0 失败：返回正常 R[]，不抛 | 现有 multicast_func_predicate（不破） |
| 1 失败：抛 `MulticastException<int>`，`Results[idx]=0`，`Failures.Length=1` | 新 multicast_func_aggregate |
| `MulticastPredicate<int>.Invoke(arg, true)` 1 失败：抛 `MulticastException<bool>`，`Results[idx]=false` | 新 multicast_predicate_aggregate |
| `continueOnException=false`：fail-fast 不变 | 现有 golden 覆盖 |
