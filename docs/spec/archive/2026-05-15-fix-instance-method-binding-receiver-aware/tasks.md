# Tasks: fix instance method binding (receiver-aware)

> 状态：🟢 已完成 | 创建：2026-05-15 | 完成：2026-05-15 | 类型：fix
> Spec 类型：minimal mode（1 file fix + tests + doc — per workflow.md "fix"）

## 进度概览

- [x] 阶段 1: 实现 `ReceiverChainHasMethod` + 替换条件
- [x] 阶段 2: 单元测试 —— skipped（golden e2e 在阶段 3 提供足够覆盖；编译期 helper 没有独立 reachability 不经 e2e）
- [x] 阶段 3: 端到端 golden 测试（`src/tests/classes/method_binding_receiver_aware.z42`）
- [x] 阶段 4: 文档同步（compiler-architecture.md "Instance method binding" 段）
- [x] 阶段 5: 恢复 z42.json（独立 commit，但与本 fix 一起 ship）
- [x] 阶段 6: GREEN + 归档 + commit

## 阶段 1: 实现

- [ ] 1.1 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs)
  - 在 `EmitInstanceBoundCall` 中：
    - 加 private helper `ReceiverChainHasMethod(string receiverClass, string methodName) -> bool`
    - helper 用 `_ctx.QualifyClassName(receiverClass)` → walk `ClassRegistry.TryGetMethods` + `TryGetBaseClassName` 直到 hit 或 root
    - 替换 line 112-114 条件：`!receiverIsLocalClass` → `!receiverClassOwnsMethod`
    - 更新注释说明 receiver-aware rationale + 删除现有 L3-G4d Stack 例子（保留为历史 reference）

## 阶段 2: 单元测试

- [ ] 2.1 NEW or MODIFY 测试文件（具体位置看现有 FunctionEmitter 测试在哪）：
  - test_receiver_owns_method_skips_depindex：模拟一个 BoundCall 其中 ReceiverClass 的 ClassRegistry 含 MethodName，DepIndex 也含 MethodName。验证 emit 出 `VCallInstr` 而不是 `CallInstr`。
  - test_receiver_inherits_method_skips_depindex：receiver 自己没有 method 但 base class 有 → 走 v_call。
  - test_no_receiver_method_uses_depindex：receiver class 在 ClassRegistry 中但没有 MethodName → 走 DepIndex。
  - test_unknown_receiver_uses_depindex：ReceiverClass = null（Unknown / 推断不出）→ 走 DepIndex（与现状一致）。

## 阶段 3: 端到端 / 集成

- [ ] 3.1 现存 z42.toml 测试必须保持全绿（22 个测试，特别是 stringify 在 z42.json 同 workspace 时）
- [ ] 3.2 NEW e2e 复现：一个 user `.z42` 文件定义 `class Box { bool ContainsKey(string k); }`，imports `Std.Collections.Dictionary`，把两个用在同一函数里。两个 ContainsKey 各自走自己类的实现。
  - 位置候选：`src/tests/<category>/instance-method-binding-collision/source.z42` + `expected_output.txt`
- [ ] 3.3 跑 `./scripts/test-all.sh`（dotnet test + cargo test + test-stdlib + test-vm + test-cross-zpkg）—— 没有 pre-existing baseline 之外的回归

## 阶段 4: 文档

- [ ] 4.1 MODIFY [docs/design/compiler/compiler-architecture.md](../../design/compiler/compiler-architecture.md) 加 "Instance method binding rules"段：
  - 优先级：receiver class own methods > inherited base methods > DepIndex by name
  - 为什么 receiver-aware：避免 method-name collision 跨类劫持
  - 引用前置 spec（本 spec）

## 阶段 5: 恢复 z42.json（独立 sub-task）

仅在阶段 1-4 全绿后启动。z42.json source 仍在 `src/libraries/z42.json/`（git 未跟踪）。

- [ ] 5.1 重命名 JsonValue 方法回简洁形式：
  - `JsonGet` → `Get`、`JsonSet` → `Set`、`JsonContainsKey` → `ContainsKey`
  - `JsonAt` → `At`、`JsonAdd` → `Add`、`JsonLength` → `Length`、`JsonCount` → `Count`
  - `ObjectKeys` → `Keys`
  - `WriteJsonValue` → `Write`（in JsonWriter）
  - `ParseJsonRoot` → `ParseDocument`（in JsonParser）
- [ ] 5.2 同步更新 5 个测试文件中的方法名
- [ ] 5.3 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.json"` 回 default-members
- [ ] 5.4 MODIFY `scripts/build-stdlib.sh` 加 `z42.json` + `Std.Json` index.json
- [ ] 5.5 NEW `docs/design/stdlib/json.md` + `src/libraries/z42.json/README.md`
- [ ] 5.6 MODIFY `docs/design/stdlib/roadmap.md` + `organization.md` + `src/libraries/README.md`
- [ ] 5.7 `./scripts/test-stdlib.sh z42.json` 全绿 + 全 stdlib 不回归
- [ ] 5.8 mv `docs/spec/changes/add-z42-json/` → `docs/spec/archive/2026-05-15-add-z42-json/`
- [ ] 5.9 commit z42.json 恢复（独立于本 spec 的 fix commit）

## 阶段 6: GREEN + 归档（本 spec 的 fix 部分）

- [ ] 6.1 `dotnet test` 全绿
- [ ] 6.2 `./scripts/test-vm.sh` 全绿（baseline 不变）
- [ ] 6.3 `./scripts/test-stdlib.sh` 全绿（27/27 包括 z42.toml stringify 测试 — 验证 fix 起效）
- [ ] 6.4 mv `docs/spec/changes/fix-instance-method-binding-receiver-aware/` → `docs/spec/archive/2026-05-15-fix-instance-method-binding-receiver-aware/`
- [ ] 6.5 commit: `fix(compiler): instance method binding now receiver-aware`
- [ ] 6.6 push origin main

## 备注

### Why minimal-mode vs full-flow

Per workflow.md, this is `fix` type (Bug fix, not lang/ir/vm semantic change). The IR / VM stays unchanged — `v_call` and `call` opcodes work the same as before. Only the compiler's binding decision changes.

Risk: subtle binding semantics. Mitigation: comprehensive unit + e2e tests above.

### Why not also fix DepIndex itself

The DepIndex is correctly storing all imported methods by name. The hijack happens at the **lookup** site (FunctionEmitterCalls), where we don't consult the receiver's class table. Fixing the lookup site is surgical; rebuilding DepIndex to be receiver-aware would be a much bigger refactor and changes the API surface for other call sites that legitimately use name-only lookup.

### Connection to z42.json blocker

z42.json (in `docs/spec/archive/2026-05-14-add-z42-json/` once paused-spec is archived) is blocked by exactly this bug. Phase 5 of this spec is the recovery — once binding is receiver-aware, JsonValue can use clean method names (Get/Set/Length/...) without `Json*` prefix.
