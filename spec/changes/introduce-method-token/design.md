# Design: introduce-method-token (Phase 1)

## Architecture

```
                        Module load
                            │
              merge_modules │  (existing)
                            ▼
              ┌─────────────────────────┐
              │  resolver::             │  (NEW)
              │   resolve_module(&mut   │
              │                  Module)│
              └─────────────────────────┘
                            │
                            │ for each Function:
                            │   for each Block:
                            │     for each Instruction:
                            │       resolve string ref → token
                            │       store in Function.resolved
                            ▼
              ┌─────────────────────────┐
              │  Function.resolved:     │
              │    OnceCell<ResolvedTokens>
              │    │                    │
              │    ├─ method_tokens:    │  per Call site
              │    │     Vec<MethodId>  │
              │    ├─ builtin_tokens:   │  per Builtin site
              │    │     Vec<BuiltinId> │
              │    ├─ type_tokens:      │  per ObjNew site
              │    │     Vec<TypeId>    │
              │    └─ vcall_ic:         │  inline cache (Cell)
              │          Vec<VCallIC>   │
              └─────────────────────────┘
                            │
                            ▼
                       Interp dispatch
                            │
                            ├─ Cache hit (token != UNRESOLVED)
                            │     → module.functions[id.0 as usize]
                            │
                            └─ Cache miss (token == UNRESOLVED)
                                  → fallback string lookup
                                  → write resolved id back to cache
                                  → continue dispatch
```

## Decisions

### Decision 1: Eager 还是 lazy resolution？

**问题**: 加载期 `resolve_module` 是预先解析所有可解析的 token（eager），还是首次执行时按需解析（lazy）？

**选项**:
- **A. Eager** at module load — `merge_modules` 后立刻扫所有指令，能解析的全填，cross-zpkg 留 UNRESOLVED
- **B. Lazy** at first hit — 首次 dispatch 时从 string 解析，填回；冷代码不付解析成本

**决定**: 选 **A (Eager)**。原因:
1. 解析成本相对一次性（一个 module 几千条 instruction × 一次 hash），分摊到一次启动 vs 每次 first-call
2. Eager 让所有 cache slot 在 module ready 时已最大化填充，cross-zpkg 之外无 first-hit overhead
3. 简化推理：cache 状态仅在 lazy load 触发时变化（cross-zpkg 解析），代码路径更清晰
4. Memory 成本可控：典型 module 几 KB token 数据

### Decision 2: Token 存储——内嵌 Instruction vs 外置 ResolvedTokens

**问题**: token cache 放哪？

**选项**:
- **A. 内嵌 Instruction**：每个 Instruction 加 `cached_token: AtomicU32` 字段
- **B. 外置 per-Function**：Function 持有 `resolved: OnceCell<ResolvedTokens>`，里面是 parallel array
- **C. 外置 per-Module**：Module 持有 flat `instruction_tokens: Vec<u32>`，按全局 instruction index 索引

**决定**: 选 **B (外置 per-Function)**。原因:
1. 不需要改 Instruction struct（避免 bincode 兼容性 risk）
2. ResolvedTokens 是纯运行时辅助数据，`#[serde(skip)]` 不持久化
3. per-Function 粒度比 per-Module 简单（每函数自己负责自己的 cache）
4. OnceCell 保证 thread-safe 单次初始化（虽然现在单线程，留 hook）

### Decision 3: 跨 zpkg 解析时序

**问题**: 跨 zpkg Call 在 lazy_loader 触发后如何回填 token cache？

**选项**:
- **A. 解析后只填本次 call site**：首次成功 dispatch 后写回该 site 的 cache
- **B. lazy load 完成时全 module 重 resolve**：触发 `resolve_module` 第二次扫描所有 cache UNRESOLVED 项

**决定**: 选 **A (单点回填)**。原因:
1. 简单：每个 site 自己负责自己的 cache，无 module-wide pass 协调
2. 实际命中分布：lazy load 一次但调用多次，单点回填后续命中即可
3. 回填竞态用 OnceCell 内的 Cell<u32> + AtomicU32 保证 idempotent

### Decision 4: VCall 的 inline cache 设计

**问题**: VCall 的 dispatch 依赖 receiver runtime type，不能纯 per-site 解析。如何 cache？

**选项**:
- **A. Monomorphic IC**：每 VCall site 一个 slot，缓存最近一次 (TypeId, vtable_slot)；receiver type 不变即命中
- **B. Polymorphic IC**：每 site 一个小 array (typically 4 entries) of (TypeId, slot)；linear search
- **C. 全局 dispatch table**：(TypeId × method_name_id) → slot 二维查表

**决定**: 选 **A (Monomorphic)** for Phase 1。原因:
1. 现有 z42 stdlib + tests 大多数 VCall site 是 monomorphic（review.md 未提到 megamorphic 痛点）
2. Mono IC 实现成本最低，性能优势仍显著
3. Phase X (future) 升级到 Poly IC 是渐进式扩展（同接口，不同实现）

VCallIC 数据结构:
```rust
pub struct VCallIC {
    cached_type: Cell<u32>,         // TypeId.0; UNRESOLVED = 未命中过
    cached_slot: Cell<u32>,         // VTableSlot.0
    cached_fn_idx: Cell<u32>,       // MethodId.0 of resolved target
}
```

dispatch:
```rust
let recv_type_id = obj.type_desc.id;  // TypeDesc 加 id 字段
let ic = &resolved.vcall_ic[site_idx];
if ic.cached_type.get() == recv_type_id.0 {
    // hit — direct call
    module.functions[ic.cached_fn_idx.get() as usize]
} else {
    // miss — walk hierarchy + update IC
    let (slot, fn_idx) = resolve_virtual(...)?;
    ic.cached_type.set(recv_type_id.0);
    ic.cached_slot.set(slot);
    ic.cached_fn_idx.set(fn_idx);
    module.functions[fn_idx as usize]
}
```

### Decision 5: BuiltinId 全局还是 per-VmContext

**问题**: `BuiltinId` 表生命周期？

**选项**:
- **A. 全局静态** `static BUILTINS: &[(name, fn)]`
- **B. per-VmContext** `Vec<BuiltinFn>` field

**决定**: 选 **A (全局静态)**。原因:
1. Builtin 是 stateless 函数指针；多 VmContext 共享是安全的
2. memory feedback "VmContext 多实例隔离" 是针对 mutable runtime state；BUILTINS 不可变
3. 简单且零开销

### Decision 6: Field/Static 字段访问要不要也 token 化？

**问题**: `FieldGet { field_name: String }` 等同样 string-based。

**决定**: **本 spec 不动 Field/Static**。Field 解析依赖 receiver type（同 VCall 的复杂度）；Phase 1 优先函数 dispatch（影响最大）。Field 留 `introduce-field-token` follow-up spec。

### Decision 7: TypeDesc 加 `id: TypeId` 字段

**问题**: VCall IC 需要按 TypeId 比较 receiver type；`TypeDesc.name` 比较太慢。

**决定**: TypeDesc 加 `id: TypeId` 字段，由 resolver 在 `merge_modules` 后分配（按 `module.classes` 顺序，0..N）。比较 `obj.type_desc.id.0 == ic.cached_type` 是单 u32 等价比较。

```rust
pub struct TypeDesc {
    pub name: String,            // 仍保留，用于诊断
    pub id: TypeId,              // NEW — runtime token
    // ... 其他字段不变
}
```

## Implementation Notes

### `metadata/tokens.rs`

```rust
//! Runtime tokens for dispatch—stable within one VmContext lifetime,
//! NOT persisted to zbc / cross-process. See spec introduce-method-token.

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct MethodId(pub u32);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct TypeId(pub u32);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct BuiltinId(pub u32);

// Sentinel: token unresolved (e.g. cross-zpkg lazy target).
pub const UNRESOLVED: u32 = u32::MAX;

impl MethodId {
    pub const UNRESOLVED: Self = Self(UNRESOLVED);
    pub fn is_resolved(self) -> bool { self.0 != UNRESOLVED }
}
// (同 TypeId / BuiltinId)
```

### `metadata/resolver.rs`

```rust
pub struct ResolvedTokens {
    /// Per Call instruction site (flat index across all blocks): MethodId.
    pub method_tokens: Vec<Cell<u32>>,
    /// Per Builtin instruction site: BuiltinId. Resolves at load (panic if miss).
    pub builtin_tokens: Vec<u32>,  // immutable after load
    /// Per ObjNew instruction site: TypeId.
    pub type_tokens: Vec<Cell<u32>>,
    /// Per VCall instruction site: monomorphic inline cache.
    pub vcall_ic: Vec<VCallIC>,
}

pub fn resolve_module(module: &mut Module) {
    // 1. Assign TypeId to each TypeDesc (incremental ID).
    // 2. For each Function:
    //    a. Walk all blocks, all instructions.
    //    b. For each token-bearing instruction, look up name → id.
    //    c. Build ResolvedTokens, store in Function.resolved (OnceCell::set).
    // 3. For Builtin: panic on miss (closed set).
}
```

### Interp dispatch hot path

```rust
// exec_call.rs::call (rewritten)
pub(super) fn call(
    ctx: &VmContext, module: &Module, frame: &mut Frame,
    dst: u32, fname: &str, args: &[u32],
    site_idx: u32,           // NEW — instruction position within current Function
) -> Result<Option<Value>> {
    let arg_vals = collect_args(&frame.regs, args)?;
    let resolved = ctx.current_function_resolved();   // 通过 ctx / frame 拿到
    let token = &resolved.method_tokens[site_idx as usize];
    
    let callee_idx = token.get();
    let callee = if callee_idx != UNRESOLVED {
        // hot path: direct index
        &module.functions[callee_idx as usize]
    } else {
        // miss: fallback string lookup
        let fn_idx = match module.func_index.get(fname) {
            Some(&i) => i,
            None => {
                // cross-zpkg lazy load
                if let Some(lazy_fn) = ctx.try_lookup_function(fname) {
                    return /* ... existing lazy dispatch ... */;
                }
                bail!("undefined function `{fname}`");
            }
        };
        token.set(fn_idx as u32);          // 回填 cache
        &module.functions[fn_idx]
    };
    
    let outcome = super::exec_function(ctx, module, callee, &arg_vals)?;
    // ... 同原逻辑
}
```

### Site index 来源

每个 instruction 在 Function 内的"site index" 需要传入 helper。两种获取方式:
- **A**: 调用方 (`exec_function`) 维护 `(block_idx, instr_idx) → flat_idx` 映射，传给 helper
- **B**: 在 ResolvedTokens 内部建 `instruction_to_site: Vec<Vec<u32>>`，运行时通过 (block_idx, instr_idx) 查 site_idx

**决定**: 选 **A**。site_idx 的语义是"按 token 类别 enumerate"——比如这是函数内第 3 个 Call instruction 就是 method_tokens[2]。`exec_function` 进入函数前预计算每条指令的 site_idx。或者更简单：每条指令传 site_idx 到 helper，预计算放在 ResolvedTokens 内的 `Vec<Option<SiteRef>>` 与 instruction 平行。

最终方案: ResolvedTokens 内每个 token 表 + 一个并行 `Vec<u32>` 给定每条指令的 site index：
```rust
pub struct ResolvedTokens {
    pub method_tokens: Vec<Cell<u32>>,  // 长度 = Call site 总数
    pub builtin_tokens: Vec<u32>,       // 长度 = Builtin site 总数
    pub type_tokens: Vec<Cell<u32>>,    // 长度 = ObjNew site 总数
    pub vcall_ic: Vec<VCallIC>,         // 长度 = VCall site 总数
    // 每条指令对应的 site index（按指令类型）：
    // (block_idx, instr_idx) → site_idx；只为 token-bearing instructions 填值。
    pub site_index: Vec<Vec<u32>>,
}
```

`exec_instr` 接收 (block_idx, instr_idx) → 查 `resolved.site_index[block_idx][instr_idx]` 拿 site_idx。

实施细节进一步在 tasks.md。

## Testing Strategy

- 单元测试 (Rust):
  - `tokens.rs` newtype 类型安全（compile-time）
  - `resolver.rs::resolve_module`：
    1. 模块加载后所有可解析 Call → MethodId 命中
    2. cross-zpkg Call → UNRESOLVED
    3. Builtin 全部命中（如果某 builtin 名 typo，panic）
    4. ObjNew → TypeId 命中
    5. VCall site 有 IC slot
- 集成测试: `tests/method_token_resolution.rs`
  - 加载多个 zpkg，验证 token 状态
  - 触发 cross-zpkg dispatch，验证 cache 回填
  - 多态 VCall 不崩溃（IC miss path 工作）
- VM golden: 现有 310 个 case 不应回归（dispatch 行为不变，仅快路径加速）
- dotnet test: 应不受影响（compiler 端不动）

## Risks

1. **Instruction enum / Block 结构改 OnceCell 字段，bincode 兼容性**
   - 缓解: `Function.resolved: OnceCell<ResolvedTokens>` 加 `#[serde(skip, default)]`，序列化时跳过
   - 验证: 所有 zbc 文件 deserialize 后 `resolved` 是 None；`resolve_module` 在 load 后填
   
2. **OnceCell 与多线程**
   - 当前 z42 单线程，OnceCell 不会并发；但留 hook 用 `OnceLock` (sync 版本) 而非 `OnceCell`
   - 实施时统一用 `std::sync::OnceLock`

3. **Cell<u32> 对 cross-zpkg 回填的 thread-safety**
   - 当前单线程：`Cell<u32>` 安全
   - 未来多线程：用 `AtomicU32`（zero-cost upgrade）
   - 实施时统一用 `AtomicU32`，避免 future migration

4. **TypeDesc 加 id 字段对其他消费者影响**
   - native interop / reflection 代码中读 TypeDesc 的地方需要新字段
   - 默认 id = `TypeId(UNRESOLVED)`，consumer 不读则无影响
   - 验证: native_interop_e2e.rs 测试不应回归
