# Tasks: introduce-method-token (Phase 1)

> 状态：📋 设计就绪，待实施 | 创建：2026-05-08
> 类型：vm（runtime dispatch 契约扩展，走完整流程）
> 来源：[review.md](../../../docs/review.md) Part 4 §4.1 + §4.6 + §4.7（tier-up 前置）

## 进度概览
- [ ] 阶段 1: 基础类型 + 全局 BuiltinTable
- [ ] 阶段 2: TypeDesc.id + ResolvedTokens 结构
- [ ] 阶段 3: 加载期 resolver
- [ ] 阶段 4: Interp dispatch 改造（Call / Builtin / ObjNew / VCall）
- [ ] 阶段 5: 测试 + 验证
- [ ] 阶段 6: 文档同步
- [ ] 阶段 7: 归档 + 提交

## 阶段 1: 基础类型 + 全局 BuiltinTable

- [ ] 1.1 `src/runtime/src/metadata/tokens.rs` (NEW): 定义 `MethodId(u32)` / `TypeId(u32)` / `BuiltinId(u32)` / `FieldId(u32)` / `VTableSlot(u32)` 五个 newtype，含 `UNRESOLVED: u32 = u32::MAX` 常量 + `is_resolved()` 方法
- [ ] 1.2 `metadata/mod.rs` 加 `pub mod tokens;`
- [ ] 1.3 `corelib/dispatch_table.rs` (NEW): 改造现有 `exec_builtin(ctx, name, args)` —— 把所有 builtin 函数收成 `static BUILTINS: &[(&'static str, BuiltinFn)]` 静态表，并提供 `builtin_id_of(name) -> Option<BuiltinId>` 名查 + `exec_builtin_by_id(ctx, id, args)` id 查
- [ ] 1.4 保留 `exec_builtin_by_name(ctx, name, args)` 作 fallback / 测试入口

## 阶段 2: TypeDesc + Function ResolvedTokens

- [ ] 2.1 `metadata/types.rs::TypeDesc` 加 `pub id: TypeId` 字段（默认 `TypeId(UNRESOLVED)`，由 resolver 分配）
- [ ] 2.2 `metadata/bytecode.rs::Function` 加 `#[serde(skip, default)] pub resolved: std::sync::OnceLock<ResolvedTokens>` 字段
- [ ] 2.3 `metadata/resolver.rs` (NEW): 定义 `ResolvedTokens` struct（method_tokens / builtin_tokens / type_tokens / vcall_ic / site_index）+ `VCallIC` struct（cached_type_id / cached_slot / cached_fn_idx，用 `AtomicU32`）
- [ ] 2.4 `metadata/mod.rs` 加 `pub mod resolver;`

## 阶段 3: 加载期 resolver

- [ ] 3.1 `resolver::resolve_module(&mut Module)`:
  - **Step A**: 给每个 `module.classes[i]` 对应的 `TypeDesc.id = TypeId(i as u32)`
  - **Step B**: 对每个 Function:
    - 扫所有 (block_idx, instr_idx, instr) 三元组
    - 按指令类型 enumerate token site:
      - `Call` → 加 method_tokens 一项；`module.func_index[func]` 命中 → `MethodId(idx)`，否则 `UNRESOLVED`
      - `Builtin` → 加 builtin_tokens 一项；`builtin_id_of(name)` 命中 → `BuiltinId(idx)`，未命中 panic
      - `ObjNew` → 加 type_tokens 一项；`module.type_registry[class_name]` → `TypeId`
      - `VCall` → 加 vcall_ic 一项（初始 UNRESOLVED）
    - 同时填 `site_index[block_idx][instr_idx] = site_idx`（每指令类型独立编号）
  - **Step C**: `function.resolved.set(...)` —— OnceLock 单次填充
- [ ] 3.2 `loader.rs::merge_modules` 末尾调 `resolver::resolve_module(&mut module)`
- [ ] 3.3 unit test：`resolver::tests::resolve_module_*` 覆盖：
  - intra-module Call 命中
  - cross-zpkg Call 留 UNRESOLVED
  - Builtin 命中
  - 未注册 builtin name → panic
  - ObjNew → TypeId

## 阶段 4: Interp dispatch token cache hot path

- [ ] 4.1 `interp/exec_instr.rs::exec_instr` 接收 (block_idx, instr_idx) 参数；从 `resolved.site_index[block_idx][instr_idx]` 拿 site_idx，传给 helper
- [ ] 4.2 `interp/mod.rs::exec_function` 主循环传 (block_idx, instr_idx) 给 exec_instr
- [ ] 4.3 `interp/exec_call.rs::call`: 接 site_idx；先读 `resolved.method_tokens[site_idx]`；命中走 fast path；miss 走 string lookup + 回填
- [ ] 4.4 `interp/exec_call.rs::builtin`: 接 site_idx；读 `resolved.builtin_tokens[site_idx]`（一定已解析）；调 `exec_builtin_by_id`
- [ ] 4.5 `interp/exec_object.rs::obj_new`: 接 site_idx；读 type_tokens[site_idx]
- [ ] 4.6 `interp/exec_vcall.rs::vcall`: 接 site_idx；读 vcall_ic[site_idx]；命中（receiver type 与 cache 一致）走 cache slot；miss 走 hierarchy walk + IC update
- [ ] 4.7 lazy_loader 路径不变，但成功后**回填**该 site 的 token slot

## 阶段 5: 测试 + 验证

- [ ] 5.1 `cargo build --manifest-path src/runtime/Cargo.toml` 0 warning
- [ ] 5.2 `cargo test`: 全绿（含新 resolver tests）
- [ ] 5.3 `tests/method_token_resolution.rs` (NEW) 集成测试 (~6 case):
  - 单 zpkg 加载，所有 token 应解析
  - 多 zpkg + lazy load，cross-zpkg call 触发 lazy → 回填
  - VCall monomorphic IC 命中
  - VCall megamorphic IC miss 不崩溃（行为不变）
  - Builtin 全部命中
- [ ] 5.4 `./scripts/test-vm.sh`: 310/310 全绿（dispatch 行为不变，仅 hot path 加速）
- [ ] 5.5 `dotnet test`: 1109/1109 全绿（compiler 不动）
- [ ] 5.6 性能验证（可选）: `criterion` 跑现有 `benches/smoke_bench.rs`，对比 dispatch 热路径 throughput

## 阶段 6: 文档同步

- [ ] 6.1 `docs/design/vm-architecture.md` 新增 "Method Token System" 章节：MethodId/TypeId/BuiltinId 定义 + ResolvedTokens 结构 + 解析时序 + 与 D1b func_ref_slots 关系
- [ ] 6.2 `src/runtime/src/metadata/README.md` (如存在) 同步核心文件表加 tokens.rs / resolver.rs
- [ ] 6.3 `docs/review.md` Part 4 §4.1 + §4.6 状态注记 → 🟢
- [ ] 6.4 `docs/deferred.md` 加 D-XX 条记录 Phase 2 (formalize-jit-method-token) / Phase 3 (zbc-token-format-bump) / Phase 4 (compiler-token-emit) 后续 spec 待启动

## 阶段 7: 归档 + 提交

- [ ] 7.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 7.2 `spec/changes/introduce-method-token/` → `spec/archive/YYYY-MM-DD-introduce-method-token/`
- [ ] 7.3 commit + push

## 备注

- **本 spec 仅 Phase 1**：interp + 解析基础设施。JIT helper 改造为 sibling spec `formalize-jit-method-token` (Phase 2)，zbc 格式 bump 为 future spec (Phase 3)，compiler-side token emit 为 future spec (Phase 4)
- **依赖关系**：本 spec 必须早于 reflection R-series（M7）开始；后两 phase 不阻塞 R-series
- **预计工作量**：4-6 小时实施 + 2-3 小时调试回归（基于 review.md 描述与现有代码规模评估）
- **回归风险**：cross-zpkg 时序细节 + Function.resolved OnceLock 初始化时序。tests 覆盖应包含多 zpkg + lazy load 场景
- **OnceLock vs OnceCell**：用 `std::sync::OnceLock`（thread-safe）而非 `OnceCell`，为未来多线程留 hook
- **Cell<u32> vs AtomicU32**：用 `AtomicU32`（zero-cost on x86/ARM under single-thread），future-proof
