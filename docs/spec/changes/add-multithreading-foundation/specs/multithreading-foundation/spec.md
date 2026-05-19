# Spec: Multi-threading foundation

## ADDED Requirements

### Requirement: VmCore / VmContext 类型层划分

#### Scenario: VmCore 是 Send + Sync
- **WHEN** 单元测试 `vm_core_is_send_sync` 调用 `fn assert_send_sync<T: Send + Sync>() {}` with `VmCore`
- **THEN** 编译通过

#### Scenario: VmContext 持 `Arc<VmCore>`
- **WHEN** 启动 VM 通过 `VmContext::new(...)` 路径
- **THEN** 内部产生**唯一**的 `VmCore` 实例
- **AND** 任何后续 `VmContext::clone()`（用于跨线程视图）增加 `Arc<VmCore>` strong count，不复制 VmCore 状态
- **AND** 共享字段（static fields / type registry / lazy loader / native libs / pinned buffers / process slots / GC backend）从 VmCore 读取

#### Scenario: per-thread 字段不在 VmCore
- **WHEN** 任意线程修改 `call_stack` / `pending_exception` / `frame_guards` / `func_ref_slots`
- **THEN** 其他线程的同名字段**不受影响**
- **AND** 这些字段类型保留 `Rc<RefCell<T>>`（单线程内便宜）

### Requirement: GcRef Send + Sync

#### Scenario: GcRef<T> is Send when T: Send
- **WHEN** `T: Send`（如 `Vec<Value>` 满足条件因为 Value 之后也会 Send）
- **THEN** `GcRef<T>: Send` 编译期成立

#### Scenario: GcRef<T> is Sync when T: Send
- **WHEN** 同上
- **THEN** `GcRef<T>: Sync` 成立（Mutex 提供 Sync 保证）

#### Scenario: borrow / borrow_mut 保持 API
- **WHEN** 用户代码调 `gc_ref.borrow()` 或 `gc_ref.borrow_mut()`
- **THEN** 返回值 type 名字不变（仍叫 `Ref<'a, T>` / `RefMut<'a, T>` —— alias 到 `MutexGuard<'a, T>`）
- **AND** 单线程使用语义不变
- **AND** 递归借用（同一线程内重入 borrow_mut）panic（同 RefCell 行为）

#### Scenario: GcAllocation Drop 仍触发 finalizer
- **WHEN** 最后一个 `GcRef<T>` 引用 drop
- **AND** 该对象已注册 finalizer
- **THEN** finalizer 在 Drop 中一次性触发（同 Phase 3e 语义）

### Requirement: MagrGC trait Send + Sync

#### Scenario: MagrGC: Send + Sync 必选
- **WHEN** trait `MagrGC` 被显式实现
- **AND** 实现类型不是 Send + Sync
- **THEN** 编译期错误

#### Scenario: ArcMagrGC 满足边界
- **WHEN** 实例化 `ArcMagrGC::new(...)`
- **AND** 静态 assert `ArcMagrGC: Send + Sync`
- **THEN** 通过

### Requirement: 单线程行为不变（不可回归契约）

#### Scenario: 所有现有 stdlib 测试照绿
- **WHEN** `./scripts/test-stdlib.sh` 执行
- **THEN** 17 lib × 62+ files 全部通过

#### Scenario: VM e2e 不回归
- **WHEN** `./scripts/test-vm.sh`
- **THEN** interp 156/156 + JIT 156/156 = 312/312（数量与本 spec 实施前一致；JIT 路径只要重 compile 仍 GREEN 即可）

#### Scenario: 编译器 1288 单测不回归
- **WHEN** `dotnet test src/compiler/z42.Tests/`
- **THEN** 1288/1288

#### Scenario: GC unit tests 全绿
- **WHEN** `cargo test --release --manifest-path src/runtime/Cargo.toml gc::`
- **THEN** 全部 PASS（具体数量按落地时实际 count）

#### Scenario: Phase 3e finalizer 行为不变
- **WHEN** 用户脚本 register finalizer 并 drop 最后引用
- **THEN** finalizer 触发一次（同 Phase 3e 测试）

### Requirement: 性能基线声明

#### Scenario: GcRef clone 不超过 baseline 2x
- **WHEN** 跑 GC stress micro-benchmark（如有）或 stdlib heavy-Value-clone 测试（如 stringbuilder）
- **THEN** wall-clock 时间不超过本 spec 实施前 2.0 倍
- **AND** 若超过 2.0 倍，提案要点：升级到 `parking_lot` 或重新评估 backing 选型

> Arc::clone 比 Rc::clone 多一对原子 RMW。x86_64 单核约 ~1-3 ns 差别；ARM 略多。Value clone 在 VM 热路径但不在每条指令——预期总开销 < 10%。

## IR Mapping

无新 opcode。本 spec 纯 runtime 内部 refactor。

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] **GC trait + GcRef backing**
- [x] **VmContext / VmCore 类型重组**
- [x] interp 调用方更新（`.borrow()` → `.lock()` 机械替换）
- [x] corelib NativeFn 更新（同上）
- [x] host embedding 接口更新（同上）
- [ ] JIT —— 同步更新但单测覆盖 best-effort（feature-flag 后置）

## Anti-Scope（显式不在本 spec）

| 项 | 应该去哪 |
|---|---|
| `Std.Threading.Thread.Start(action)` | 下个 spec `add-threading-stdlib` |
| `Mutex<T>` / `Channel<T>` 用户类型 | `add-threading-stdlib` 之后单独 spec |
| `spawn` / `task scope` 语法 | concurrency.md 已规划，L3 |
| 并发 GC / safepoint | 单独 spec，Phase A 性能轨道 |
| `Send` / `Sync` 作为 z42 interface | concurrency.md §4，L3 |
| async / await | concurrency.md 完整方案，L3 |
| VM benchmark 基线建立 | roadmap 0.2.2 perf CI 单独项 |
