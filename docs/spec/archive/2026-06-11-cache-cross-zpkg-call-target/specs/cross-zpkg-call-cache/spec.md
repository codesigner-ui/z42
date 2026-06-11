# Spec: Cross-zpkg Call 目标 per-site 缓存

> 状态：DRAFT 待审。行为保持的 dispatch 性能优化——场景以「不变量」+「缓存命中」
> 表达，不引入新 VM 行为。

## ADDED Requirements

### Requirement: cross-zpkg Call 二次起命中 per-site 缓存

#### Scenario: 首次 cross-zpkg 调用解析并缓存
- **WHEN** interp 执行一个目标在其它 zpkg 的 `Call` site，且该 site 的
  `cross_module_targets[site]` cell 为空
- **THEN** 经 `try_lookup_function(fname)` 解析出 `Arc<Function>`，存入该 cell，
  并执行该函数（结果与缓存前逐字节相同）

#### Scenario: 复访同一 cross-zpkg site 借用缓存
- **WHEN** 同一个 `Call` site 第二次（及以后）执行，cell 已填充
- **THEN** 直接借用缓存的 `Arc<Function>`（`OnceLock::get`），**不再** 调
  `module.func_index.get` / `try_lookup_function`（零 String hash）

#### Scenario: 本模块 Call 路径不受影响
- **WHEN** 一个 `Call` site 的目标在本模块（`method_tokens[site]` 命中或 `func_index`
  命中）
- **THEN** 走原 u32 slot 快路径，**不查** cross cell（本模块命中路径零额外开销）

#### Scenario: 未定义函数仍报错
- **WHEN** 一个 `Call` 的目标既不在本模块、也无任何 zpkg 提供
- **THEN** `bail!("undefined function ...")`，与现状一致（缓存不掩盖解析失败）

## MODIFIED Requirements

### Requirement: ResolvedTokens 结构

**Before:** `ResolvedTokens { method_tokens: Vec<AtomicU32>, builtin_tokens, type_tokens, site_index, ... }`
**After:** 增加 `cross_module_targets: Vec<OnceLock<Arc<Function>>>`（与 `method_tokens`
等长、同 site 索引）。`#[serde(skip)]` 性质不变（运行期派生，不入 zbc/zpkg）。

### Requirement: 行为与格式不变

#### Scenario: vm + cross-zpkg goldens 全绿
- **WHEN** 跑 `z42 xtask.zpkg test vm` + `z42 xtask.zpkg test cross-zpkg`
- **THEN** 全部 golden / 跨包端到端结果与实施前**逐字节相同**（0 行为变化）

#### Scenario: 无格式 bump
- **WHEN** 检视 zbc/zpkg version 常量
- **THEN** 与实施前一致（`ResolvedTokens` 不进二进制格式；不触 version-bumping.md）

## IR Mapping

无新 opcode；无 zbc 变化。仅 interp dispatch 的运行期缓存结构。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen —— 不涉及
- [x] VM metadata（`resolver.rs`：ResolvedTokens 加字段 + 初始化）
- [x] VM interp（`exec_call.rs`：cross cell 命中/回填；`exec_instr.rs`：传 cell）
- [ ] VM JIT —— 不涉及（JIT cross-zpkg 走 `jit_call` helper，本变更不动）
