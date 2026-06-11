# Design: Per-site cross-zpkg `Call` target cache

> 状态：DRAFT 待审

## Architecture

```
Function.resolved: OnceLock<ResolvedTokens>   (#[serde(skip)], 运行期派生)
  ResolvedTokens {
    method_tokens:         Vec<AtomicU32>           ← 本模块 module.functions 下标缓存（已有）
    cross_module_targets:  Vec<OnceLock<Arc<Function>>>  ← 新增：cross-zpkg 目标缓存（与 method_tokens 同长、同 site 索引）
    site_index:            Vec<Vec<u32>>            ← (block,instr) → site_idx（已有）
    ...
  }

exec_instr  Call(insn) arm:
  site = site_index[block][instr]
  tok  = resolved.method_tokens.get(site)
  xcell= resolved.cross_module_targets.get(site)     ← 新增
  exec_call::call(.., fname, args, tok, xcell)

exec_call::call:
  1. 本模块快路径：tok 命中 → module.functions[idx]            （已有，不变）
  2. 本模块 miss：module.func_index.get(fname) → 命中则回填 tok （已有，不变）
  3. cross-zpkg：
       if let Some(arc) = xcell.get() { 借用 arc.as_ref() }      ← 新增命中路径（零 hash）
       else { try_lookup_function(fname) → xcell.set(arc.clone()); 借用 }  ← 首次回填
  4. 都 miss → bail "undefined function"                         （已有）
```

## Decisions

### Decision 1: per-site `OnceLock<Arc<Function>>` vs 全局整数寻址表

**问题**：cross-zpkg 目标怎么缓存才能 O(1) 复访？
**选项**：
- **A（per-site cell）**：`ResolvedTokens` 加 `Vec<OnceLock<Arc<Function>>>`，每个 Call
  site 一个 cell。首次 cross-zpkg 解析后存 `Arc`，复访借用。
- **B（全局整数寻址）**：给所有 loaded module/function 编 `(ModuleId, FuncIdx)`，slot
  扩成 `AtomicU64` 携带二元组，另建整数→`Arc<Function>` 注册表。
**决定**：**选 A**。
- B 需要一张 **全局可变** 注册表 + 并发 append 协议（VM 多线程在做，append 竞争是
  返工高发区，见 parallel-development.md "runtime 热子系统"）。
- A 是 **per-site、无共享可变**：每个 cell 独立 `OnceLock`，天然 `Sync`，无跨 site
  竞争；首次 set 用 `OnceLock` 的幂等语义兜并发（双填只有一个赢，都是同一函数无害）。
- A 命中路径是 `OnceLock::get() -> Option<&Arc<Function>>` 再 `.as_ref()`，**零拷贝、
  零 hash、零 atomic-RMW**（只一次 acquire load）。
- 代价：每个 Call site 多 `sizeof(OnceLock<Arc>)`≈16 B（指针 + once 状态）；本模块-only
  site 的 cell 永不填充（空 OnceLock），可接受。

### Decision 2: `OnceLock`（不可变缓存）vs 可失效缓存

**问题**：cross-zpkg 目标需要失效/重填吗？
**决定**：**不需要 → `OnceLock`**。一次 VM 运行内，FQ 函数名 → 目标函数是稳定映射
（lazy loader 一旦把某 zpkg 的某函数装进 `function_table`，该 `Arc<Function>` 在运行期
不变；hot-reload 是独立机制、走另一套失效路径，且当前 cross-zpkg site 也没有失效逻辑
——本变更不改变这一点）。OnceLock「一次写、多次读」正好匹配，比 `ArcSwapOption` 更省、
更简单。

### Decision 3: 缓存只在 cross-zpkg 路径，不动本模块快路径

本模块 Call 已被 `method_tokens` u32 slot 覆盖（最快：纯整数索引）。cross cell 只在
**本模块 miss 之后** 介入，不触碰、不退化已有快路径。对纯本模块程序：cross cell 全空、
零额外运行成本（仅多一个空 Vec 的初始化 + 每 Call 一次 `xcell.get()` 是 `None` 的廉价
分支——可后置到 miss 分支内，本模块命中根本不查 xcell）。

> 实施注意：把 `xcell.get()` 放在「本模块两级 miss 之后」，确保本模块命中路径一条
> 多余指令都不加。

## Implementation Notes

- `ResolvedTokens` 构造（resolver.rs pass-2）：`cross_module_targets` 按
  `method_site_names.len()` 初始化为 `(0..n).map(|_| OnceLock::new()).collect()`。
- `exec_call::call` 签名：加 `cross_cell: Option<&OnceLock<Arc<Function>>>`。`None` 时
  退回纯 `try_lookup_function`（back-compat，与现有 `method_token: None` 对称）。
- 命中借用：
  ```rust
  } else if let Some(cell) = cross_cell {
      let arc = match cell.get() {
          Some(a) => a,                                   // 命中：零 hash
          None => {
              let resolved = ctx.try_lookup_function(fname)
                  .ok_or_else(|| anyhow!("undefined function `{fname}`"))?;
              // set 幂等：并发双填取胜者；get() 必返已存值
              let _ = cell.set(resolved);
              cell.get().expect("just set")
          }
      };
      super::exec_function(ctx, module, arc.as_ref(), &arg_vals)?
  }
  ```
  （`expect` 在「刚 set 必有值」处可接受；或用 `get_or_init` 闭包返回 `Result` 的
  变体——`OnceLock::get_or_try_init` 仍 unstable，故用 set+get 模式。）
- `Arc<Function>` import：exec_call.rs 已在 cross-zpkg 路径用 `ctx.try_lookup_function`
  返回的 `Arc<Function>`（line 48 `lazy_fn.as_ref()`），类型已在手。

## Testing Strategy

- 单元/回归：cross-zpkg Call **二次调用** 命中缓存（构造两次调用同一 cross-zpkg 函数，
  断言结果一致 + 不回归）；现有 `z42 xtask.zpkg test cross-zpkg` 端到端必须全绿。
- 行为不变权威门：`z42 xtask.zpkg test vm`（interp goldens）+ `test cross-zpkg`。
  cross-zpkg 是本变更核心路径，必须跑（不能只跑 vm）。
- JIT 路径：JIT 的 cross-zpkg 走自己的 helper（`jit_call`），本变更只改 interp
  `exec_call`；JIT 行为不变（确认 jit goldens 全绿即可）。
- 不需要新 e2e 格式/语法（纯 dispatch 缓存）。
