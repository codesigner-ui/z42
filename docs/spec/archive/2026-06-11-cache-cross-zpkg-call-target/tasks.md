# Tasks: cache-cross-zpkg-call-target

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：vm（dispatch 性能优化，行为保持）
> 子系统锁：runtime（见 ACTIVE.md）。

## 进度概览
- [x] 阶段 1: ResolvedTokens 加 cross-zpkg per-site 缓存字段 + 初始化
- [x] 阶段 2: exec_call::call 命中/回填 + exec_instr 传 cell
- [x] 阶段 3: 验证（cache-cell 契约单测 + vm/cross-zpkg goldens + 无格式漂移）

## 阶段 1: ResolvedTokens（resolver.rs）
- [x] 1.1 `ResolvedTokens` 加 `cross_module_targets: Vec<OnceLock<Arc<Function>>>`（doc 注释说明 = method site 平行缓存）
- [x] 1.2 pass-2 构造时按 `method_site_names.len()` 初始化为空 `OnceLock` 向量
- [x] 1.3 `resolved_tokens_default_is_empty` 同步加 `cross_module_targets.is_empty()`

## 阶段 2: dispatch（exec_call.rs + exec_instr.rs）
- [x] 2.1 `exec_call::call` 签名加 `cross_cell: Option<&OnceLock<Arc<Function>>>`
- [x] 2.2 本模块两级 miss 之后：`cross_cell` 命中借用 / 未命中经 `try_lookup_function` 回填（OnceLock set+get 模式）；本模块快路径不动
- [x] 2.3 `exec_instr.rs` Call arm：取 `resolved.cross_module_targets.get(site_idx)` 传入
- [x] 2.4 `None` cross_cell 回退到纯 `try_lookup_function`（back-compat 对称 method_token=None）

## 阶段 3: 验证
- [x] 3.1 cargo build runtime —— 无编译错误
- [x] 3.2 cargo test runtime lib —— 764/0（含 `cross_module_target_cell_fill_once_then_borrow`）
- [x] 3.3 `test vm` —— interp 177 + JIT 169 = 346/0（0 变化）
- [x] 3.4 `test cross-zpkg` —— 2/2（impl_propagation + vcall_base_fallback，本变更核心路径）
- [x] 3.5 `docs/design/runtime/vm-architecture.md` 同步 per-site cross-zpkg 缓存机制
- [x] 3.6 spec scenarios 逐条覆盖确认

## 备注

- 测试环境注意：`signal_handler_e2e` 集成测试存在 pre-existing 环境性 hang（UE
  crash-helper 僵尸 + macOS coredump-under-load），与本变更无关；跑 cargo test 时
  按需排除该 test target（`--lib` + 相关 integration test），不阻塞本变更门禁。
- JIT cross-zpkg 走 `jit_call` helper，本变更只优化 interp；JIT 行为不变（goldens 守门）。
