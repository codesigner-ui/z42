# Proposal: Cache cross-zpkg `Call` targets per site (review.md C7)

> 状态：DRAFT 待审（阶段 6.5 前）

## Why

`exec_call::call` 的热路径用 `Function.resolved.method_tokens[site]`（`AtomicU32`）
缓存 **本模块** 的 `module.functions` 下标——本模块 Call 第二次起 O(1) 直接索引。

但 **cross-zpkg Call**（用户代码调 stdlib、或任意跨 zpkg 调用）的目标函数不在
`module.functions`，而在 lazy loader 的 `function_table: HashMap<String, Arc<Function>>`。
一个 u32 本地下标无法表示它，所以这类 site 的 token slot 永远停在 `UNRESOLVED`，
**每次调用**都走：

```rust
module.func_index.get(fname)        // ❌ miss（cross-zpkg 不在本模块）—— 1 次 String hash
  .or_else(|| ctx.try_lookup_function(fname))  // ❌ lazy loader HashMap —— 又 1 次 String hash
```

即每个 cross-zpkg 调用付 **2 次 String hash + HashMap 探测**。z42 stdlib 现已拆成
~22 个 zpkg，用户代码对 stdlib 的调用全是 cross-zpkg；热循环里反复调同一个 stdlib
函数 → 每轮迭代都重复这次查找。

review.md **C7**（对照 CoreCLR：cross-assembly call 首次未解析 → prestub → patch
call site，**第二次直接跳无 lookup**）。本变更给 cross-zpkg site 做同款 site 级缓存。

## What Changes

- `ResolvedTokens` 加一个与 `method_tokens` 平行的 per-Call-site 缓存：
  `cross_module_targets: Vec<OnceLock<Arc<Function>>>`（长度 = method site 数）。
- `exec_call::call`：本模块 miss 后，**先查 site 的 cross cell**；命中则借用缓存的
  `Arc<Function>`（零拷贝，`OnceLock::get` 返回 `&Arc`，`.as_ref()` 给 `&Function`）；
  未命中才走 `try_lookup_function`，解析成功后 `cell.set(arc.clone())`（仅首次一次
  String hash）。
- `exec_instr.rs`：把该 site 的 cross cell 引用一并传给 `exec_call::call`。

**关键不变量**：
- **行为完全不变**：纯调用目标缓存，解析结果与现在逐字节相同（vm goldens / cross-zpkg
  端到端全绿）。缓存只在 `try_lookup_function` **已经会返回** 的 site 上生效。
- **无格式变化**：`ResolvedTokens` 是 `#[serde(skip)]` 的运行期派生结构，不进 zbc/zpkg；
  无 version bump。
- **Sync 保持**：`OnceLock<Arc<Function>>` 是 `Sync`（`Function.resolved` 当前注释
  "single-thread today" 但用 `OnceLock` 保 future 多线程安全）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/resolver.rs` | MODIFY | `ResolvedTokens` 加 `cross_module_targets: Vec<OnceLock<Arc<Function>>>`；resolve 时按 method site 数初始化空 cell |
| `src/runtime/src/interp/exec_call.rs` | MODIFY | `call()` 签名加 cross-cell 参数；本模块 miss → 查/填 cross cell，命中借用缓存 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | `Call` arm 取该 site 的 cross cell 引用，传入 `exec_call::call` |
| `src/runtime/src/interp/exec_call_tests.rs` | NEW（若不存在则建） | cross-zpkg 缓存命中/回填回归单测；或并入既有 cross-zpkg e2e |
| `docs/design/runtime/vm-architecture.md` | MODIFY | 记 per-site cross-zpkg 目标缓存机制（dispatch 优化策略） |

**只读引用**：
- `src/runtime/src/vm_context.rs::try_lookup_function` — 理解 cross-zpkg 解析返回 `Arc<Function>`
- `src/runtime/src/metadata/lazy_loader.rs::function_table` — 理解 cross-zpkg 函数存储

## Out of Scope

- **VCall / 虚调用 cross-class 缓存**（review.md C5）—— 独立后续，不同 dispatch 路径。
- **load_fn / call_indirect 的 cross-zpkg 路径** —— 本变更先做最热的 `Call`；若同形可后续套用。
- **全局 (ModuleId, FuncIdx) 整数寻址表 / AtomicU64 slot** —— per-site cell 已够，不引入
  全局可变注册表（避免并发 append 复杂度）。design.md 记此权衡。
- **zbc 把 Call 目标 intern 成 id**（C4/C5 #3）—— 需 zbc bump + 编译器改动，独立大变更。

## Open Questions

- [ ] cross cell 用 `OnceLock<Arc<Function>>`（首解析后不可变）是否足够？cross-zpkg
      解析结果在一次运行内稳定（同名 → 同函数），不需要失效/重填 → OnceLock 合适。
      design.md 论证。
