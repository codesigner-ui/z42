# Spec: Method Token System (Phase 1)

## ADDED Requirements

### Requirement: Token newtypes 提供类型安全标识

新增 `MethodId(u32)` / `TypeId(u32)` / `BuiltinId(u32)` / `FieldId(u32)` / `StaticFieldId(u32)` / `VTableSlot(u32)` newtype 包装。Sentinel `UNRESOLVED: u32 = u32::MAX` 表示"尚未解析"。

#### Scenario: 不同 token 类型不可混用

- **WHEN** 编译器尝试把 `MethodId` 传给接收 `TypeId` 的函数
- **THEN** 类型不匹配，编译失败（newtype 的核心价值）

#### Scenario: Sentinel 表示未解析状态

- **WHEN** `Function.resolved` 中某条 Call instruction 的 token 字段值为 `UNRESOLVED`（u32::MAX）
- **THEN** runtime dispatch 必须 fallback 到 string lookup（cross-zpkg lazy 触发或 OnceCell 未命中）

### Requirement: 加载期 token 解析

`metadata::resolver::resolve_module(&mut Module)` 在 `merge_modules` 完成后调用，扫所有 Function 的所有 Block 的所有 Instruction，按指令类型解析对应 token：

- `Call.func` → 查 `module.func_index[func]` → `MethodId`，找不到（cross-zpkg）→ `UNRESOLVED`
- `Builtin.name` → 查全局 `BuiltinTable` → `BuiltinId`，**必须命中**（builtin 是已知集合，未命中即 bug）
- `ObjNew.class_name` → 查 `module.type_registry[name]` → `TypeId`，找不到 → `UNRESOLVED`
- `VCall.method`：跨多 type 共享 method name，**不在 resolver 阶段解析**（per-receiver dispatch，留 hot path 缓存）
- `FieldGet.field_name` / `FieldSet`：同 VCall，receiver-dependent，hot path 缓存

#### Scenario: 同模块 Call 解析为 MethodId

- **WHEN** 模块加载并 `merge_modules` 完成后，`resolve_module` 处理 `Call { func: "Foo.Bar", ... }`，且 `func_index` 含 `"Foo.Bar"` → `42`
- **THEN** `Function.resolved.method_tokens[<call_pos>]` = `MethodId(42)`

#### Scenario: 跨 zpkg Call 标记为 UNRESOLVED

- **WHEN** `resolve_module` 处理 `Call { func: "Std.Stack.Push", ... }`，但 `Std.Stack` 在 lazy_loader 未触发的 zpkg 中
- **THEN** `Function.resolved.method_tokens[<call_pos>]` = `UNRESOLVED`；首次 dispatch 时走 lazy loader + 填回 cache

#### Scenario: Builtin 必须解析成功

- **WHEN** `resolve_module` 处理 `Builtin { name: "__int_to_str", ... }`，且 `__int_to_str` 已在 `BUILTINS` 全局表注册
- **THEN** `Function.resolved.builtin_tokens[<builtin_pos>]` = 对应 `BuiltinId`

#### Scenario: 未注册 Builtin 名导致 panic

- **WHEN** `resolve_module` 处理 `Builtin { name: "__nonexistent_builtin", ... }`
- **THEN** `resolver` panic with `"unknown builtin name `__nonexistent_builtin` (typo? not registered?)"` —— builtin 是 closed set，未命中即 bug，**必须** fail loud

### Requirement: Interp 走 token cache hot path

`interp::exec_call::call` / `interp::exec_call::builtin` / `interp::exec_object::obj_new` 等 dispatch helper 在执行前先读 `Function.resolved.<token_kind>_tokens[idx]`：
- 命中（≠ UNRESOLVED）→ 直接 `module.functions[id.0 as usize]` / `BUILTINS[id.0 as usize]`，免 hash
- 未命中（= UNRESOLVED）→ 走原 string lookup；成功后**回填** cache slot

#### Scenario: 同模块 Call 命中 cache

- **WHEN** 函数体含一条 `Call { func: "Foo.Bar" }` 且 `resolved.method_tokens[<pos>]` = `MethodId(42)`
- **THEN** dispatch 不查 HashMap，直接 `module.functions[42]` 调用

#### Scenario: Cross-zpkg lazy 解析回填

- **WHEN** 函数体含一条 `Call { func: "Std.Stack.Push" }`，初始 cache UNRESOLVED；首次执行触发 lazy_loader 加载 zpkg 后，`module.func_index[name]` 已可解析
- **THEN** 该次 call 完成后 `resolved.method_tokens[<pos>]` = `MethodId(<resolved>)`；后续命中

#### Scenario: 多次执行 cache 单调演化

- **WHEN** 函数被反复调用，cache 中已解析的 token 不再变化
- **THEN** dispatch 路径始终常数复杂度

### Requirement: Builtin 全局表 per-VmContext

`BUILTINS` 全局表（`Vec<(name: &'static str, fn: BuiltinFn)>`）在 corelib 模块定义；启动时构造好。`exec_builtin(BuiltinId, args)` 接 id 直接索引。`exec_builtin_by_name(name, args)` 保留作 fallback。

#### Scenario: 多 VmContext 共享 BUILTINS 全局表

- **WHEN** 同进程内创建多个 `VmContext` 实例
- **THEN** 各 VmContext 使用同一个静态 `BUILTINS` 表（builtin 是无状态分发，全局共享 OK；与 memory feedback "VmContext 多实例隔离" 不矛盾——builtin 是函数指针，没有 per-VM 状态）

### Requirement: VCall / FieldGet / FieldSet hot-path inline cache

VCall / FieldGet / FieldSet 的 receiver type 运行时才知道，**per-instruction-position cache** 不够；需要 **per-(instruction-position, receiver-type) cache**。

简化方案：每 site 一个 slot 缓存最近一次的 `(TypeId, slot/FieldId)` pair，receiver type 命中即用，未命中走原 hierarchy walk / field_index 查 + 更新 slot。**这是 inline cache 模式（mono IC）**。

#### Scenario: 单态 VCall 命中 inline cache

- **WHEN** 同一 VCall site 被一种 receiver type（如 `Std.List<int>`）反复调用
- **THEN** 第一次执行 walk hierarchy 解析 vtable slot；后续执行直接用 cache slot

#### Scenario: 多态 VCall miss inline cache

- **WHEN** VCall site 被不同 receiver type 交替调用（megamorphic）
- **THEN** 退化为每次都 walk —— 性能不优于现状，但不更差（Phase 1 接受；Phase X 可加 polymorphic IC）

#### Scenario: 单态 FieldGet 命中 inline cache

- **WHEN** 同一 FieldGet site `obj.X` 被一种 receiver type（如 `Std.Point`）反复访问
- **THEN** 第一次执行 `obj.type_desc.field_index["X"]` → slot；后续执行直接 IC 命中，零 hash

#### Scenario: FieldSet 同模式

- **WHEN** 同一 FieldSet site `obj.Y = v` 单态使用
- **THEN** 与 FieldGet 同款 IC，命中后直接 `obj.slots[cached_slot] = v`

### Requirement: Static fields 全局编号 + Vec 索引

`StaticGet` / `StaticSet` 的 `field: String` 是全局唯一名（编译器已保证 qualified 形式）。加载期 `resolver` 扫所有 StaticGet/Set instruction，对每个 unique `field` 分配 `StaticFieldId(u32)`。

`VmContext.static_fields` 由 `HashMap<String, Value>` 改为 `Vec<Value>`（按 id 索引），同时保留 `static_field_index: HashMap<String, u32>` 给 lazy load fallback（cross-zpkg 静态字段在主 module 加载时未知）。

#### Scenario: 同模块 StaticGet 命中直接索引

- **WHEN** 加载完成后 `Function.resolved.static_field_tokens[<site_idx>]` = `StaticFieldId(7)`
- **THEN** 执行时直接 `ctx.static_fields[7]` 读取，零 hash

#### Scenario: 跨 zpkg StaticGet lazy 解析回填

- **WHEN** 主 module 引用 `Std.Math.PI`，`Std.Math` zpkg 加载前；resolver 留 `static_field_tokens[<site>]` = `UNRESOLVED`
- **THEN** 首次 dispatch 触发 `Std.Math` lazy load + `__static_init__` 执行；执行完成后 `static_field_index["Std.Math.PI"]` 已分配 ID；StaticGet 路径回填 cache，后续命中直接 Vec 索引

#### Scenario: StaticSet 同款

- **WHEN** `Std.Counter.Count = 0` 在 `__static_init__` 中执行
- **THEN** 走同款 token cache 写入路径（首次执行触发分配 + 回填，subsequent 直接索引）

## Pipeline Steps

受影响的 pipeline 阶段：
- [ ] Lexer — 不变
- [ ] Parser / AST — 不变
- [ ] TypeChecker — 不变
- [ ] IR Codegen — 不变（compiler 仍 emit string；Phase 4 才改）
- [x] **Metadata loader** — 新增 resolver pass
- [x] **VM interp** — dispatch 改 token cache 优先
- [ ] VM JIT — 不动（Phase 2 sibling spec）

## IR Mapping

无新 IR 指令。Instruction struct 不变（保留 String 字段）。新增的是**运行时辅助元数据**：
- `Function.resolved: OnceCell<ResolvedTokens>` (`#[serde(skip)]` —— 仅运行时构造)
- `ResolvedTokens` struct 持有 per-instruction-position 的 token vec

## 不在范围

- zbc 格式 bump
- Compiler 端 emit token
- JIT helper 签名变化
- Polymorphic inline cache（多态 VCall 优化）
- Cross-process / cross-version token 稳定性
