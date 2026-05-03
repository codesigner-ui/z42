# Tasks: D-1a — corelib WeakRef builtins + Std.WeakHandle

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04 | 类型：vm + stdlib

## 进度概览
- [x] 阶段 1: VM corelib + NativeData::WeakRef
- [x] 阶段 2: stdlib WeakHandle.z42
- [x] 阶段 3: 测试
- [x] 阶段 4: 验证 + 文档同步 + 归档

## 阶段 1: VM
- [x] 1.1 `metadata/types.rs` `NativeData::WeakRef(WeakRef)` variant
- [x] 1.2 `corelib/object.rs` 加 `builtin_obj_make_weak` + `builtin_obj_upgrade_weak`（含 WeakHandle TypeDesc OnceLock cache）
- [x] 1.3 `corelib/mod.rs` dispatch_table 注册 `__obj_make_weak` + `__obj_upgrade_weak`

## 阶段 2: stdlib
- [x] 2.1 NEW `WeakHandle.z42`：`public class WeakHandle` 含 static extern MakeWeak / Upgrade

## 阶段 3: 测试
- [x] 3.1 `corelib/tests.rs` +5 单元测试：Object 弱化 / 原子值 null / Upgrade 存活 / 非 handle null / Array 弱化
- [x] 3.2 NEW `weak_ref_basic/source.z42` 端到端 golden（identity 校验留单元测试）
- [x] 3.3 NEW `expected_output.txt`
- [x] 3.4 `./scripts/regen-golden-tests.sh` 124 ok

## 阶段 4: 验证 + 文档 + 归档
- [x] 4.1 `dotnet build` ✅
- [x] 4.2 `cargo test --lib` 207/207 ✅（基线 202 + 5）
- [x] 4.3 `dotnet test` 968/968 ✅（基线 967 + 1 golden 自发现）
- [x] 4.4 `./scripts/test-vm.sh` 244/244 ✅（基线 242 + interp/jit 各 1）
- [x] 4.5 `./scripts/build-stdlib.sh` 6/6 ✅
- [x] 4.6 IncrementalBuildIntegrationTests 43 → 44
- [x] 4.7 spec scenarios 逐条核对
- [x] 4.8 文档同步：
  - `docs/deferred-features.md` D-1 改写为 D-1b：D-1a 已落地，wrapper 留 follow-up
  - `docs/roadmap.md` 加 2026-05-04 行
- [x] 4.9 移动 `spec/changes/expose-weak-ref-builtin/` → `spec/archive/2026-05-04-expose-weak-ref-builtin/`
- [x] 4.10 commit + push

## 备注
- `Std.WeakRef<TD>` ISubscription wrapper 留 D-1b follow-up（依赖 delegate .Target 提取机制；Closure.env 暴露需新 builtin）
- 本 spec 提供原料（make_weak / upgrade_weak），用户已可手工写 weak 持有逻辑
- Golden 中 identity 校验改由 corelib 单元测试覆盖（z42 stdlib 跨 CU `Object.X` 调用受 SymbolTable.cs:111 限制，与 fix-delegate-reference-equality 同款 workaround）
