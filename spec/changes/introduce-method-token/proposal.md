# Proposal: introduce-method-token

## Why

z42 VM 当前**所有跨引用 dispatch 都用 String** 作为身份（review.md Part 4 §4.1）：

| IR 指令 | String 字段 | 当前 dispatch | 本 spec 处理 |
|---|---|---|---|
| `Call` | `func: String` | `module.func_index.get(fname)` HashMap 查 | ✅ MethodId |
| `CallIndirect` | (FuncRef value 携带 String) | 同上 | ✅ MethodId（fallback 路径）|
| `VCall` | `method: String` | `type_desc.vtable_index.get(method)` | ✅ VCallIC（mono inline cache）|
| `Builtin` | `name: String` | `corelib::exec_builtin` 字符串 match | ✅ BuiltinId（全局静态表）|
| `ObjNew` | `class_name` / `ctor_name: String` | `type_registry.get` + `func_index.get` | ✅ TypeId + MethodId |
| `FieldGet` / `FieldSet` | `field_name: String` | `type_desc.field_index.get` | ✅ FieldIC（mono inline cache）|
| `StaticGet` / `StaticSet` | `field: String` | `VmContext.static_fields: HashMap<String, _>` | ✅ StaticFieldId（全局编号）|
| `IsInstance` / `AsCast` | `class_name: String` | hierarchy walk by name | ⚠️ 留 Phase 1 后续讨论（hierarchy walk 本身是 String，token 化收益小）|
| `LoadFn` / `MkClos` | `fn_name: String` | 间接调用名查 | 走 MethodId 路径（已含）|

**实测每次虚调用一次 hash + 一次字符串等价比较**。`func_ref_slots`（D1b add-method-group-conversion）已经为方法组转换实现了 module-level token cache 模式，本 spec 把它**形式化并扩展到所有跨引用 dispatch**。

**触发紧迫性**:
1. **review.md §4.1 P0**——M7 启动前应完成（反射 R-series 需要稳定 token 系统暴露运行时元数据 API）
2. **review.md §4.6**——Builtin / native 也是字符串 dispatch，同 spec 一并处理
3. **未来 tier-up 基础**（review.md §4.7）——没有稳定 MethodId 无法做函数指针替换

不做后果:
- 反射 R-series 设计无 token 锚点，要么自己重发明，要么基于 String hash（性能与正确性不利）
- 热路径 dispatch 持续吃字符串哈希成本
- IR 内存表示膨胀（String 至少 24 bytes header vs u32 4 bytes）

## What Changes

### 核心动作

1. **新增 newtype 标识符**（`metadata/tokens.rs`）:
   - `MethodId(u32)` —— per module，索引 `Module.functions: Vec<Function>`
   - `TypeId(u32)` —— per module，索引 `Module.classes: Vec<ClassDef>`
   - `BuiltinId(u32)` —— global，索引 corelib dispatch table
   - `FieldId(u32)` —— per type，索引 `TypeDesc.fields: Vec<FieldSlot>`（与 VCall 同款 inline cache）
   - `StaticFieldId(u32)` —— global，索引 `VmContext.static_fields: Vec<Value>`（取代 HashMap）
   - `VTableSlot(u32)` —— per type（实际已存在为 `usize`，仅 newtype 包装）

2. **加载期 token 解析**（`metadata/loader.rs`）: `merge_modules` 后扫所有 instruction，把可解析的 string ref → token，存到 parallel `Function.resolved: ResolvedTokens` 结构（OnceCell 懒初始化或 eager 二选一）

3. **dispatch hot path 用 token**（`interp/exec_*.rs` + `jit/helpers/*`）:
   - 命中 token cache → 直接 `module.functions[id.0 as usize]`，免 hash
   - Token 未解析（cross-zpkg lazy 触发 / OnceCell 未填）→ 走原 string lookup 路径，**填回 cache**

4. **JIT helper 签名升级**: 当前 helpers 接 `(name_ptr, name_len)`，改为 `(method_id: u32)` —— Phase 2 的工作。本 spec 落 Phase 1 (interp + 解析基础设施)，JIT 同源修改在 Phase 2 (sibling spec)

5. **Builtin 全局表**: corelib 启动时把所有 `__xxx_yyy` builtin 注册到 `BUILTINS: Vec<BuiltinFn>`，分配 `BuiltinId(u32)`；`Builtin` 指令的 `name: String` 在加载期 → `BuiltinId`

6. **Field / Static 全部 token 化**（user 裁决 2026-05-08：纳入 Phase 1，原 Decision 6 翻转）:
   - **Instance fields** `FieldGet` / `FieldSet`: per-site `FieldIC { cached_type_id, cached_slot }` 镜像 VCallIC——同款 monomorphic inline cache 模式；首次执行 walk `obj.type_desc.field_index` + 填 IC，后续 receiver type 不变即命中
   - **Static fields** `StaticGet` / `StaticSet`: 加载期扫所有 StaticGet/Set instruction，对每个 unique `field: String` 分配 `StaticFieldId(u32)`；`VmContext.static_fields: HashMap<String, Value>` 改为 `Vec<Value>`（按 id 索引）；name→id 映射保留作 lazy load fallback
   - **`vtable_index` / `field_index`** 现有 HashMap 保留作 string fallback（cross-zpkg lazy load 时仍需 name 查）

### Phase 化

| Phase | 内容 | scope |
|---|---|---|
| **1**（本 spec）| Token newtypes + 加载期解析 + interp dispatch token cache + builtin 全局表 + **Field IC + Static field 全局编号** | metadata + interp + corelib + vm_context |
| **2** sibling spec | JIT helper signatures take MethodId / BuiltinId / FieldId / StaticFieldId | jit/helpers |
| **3** future | zbc 格式 bump，IR struct 字段从 String 改 u32 token | bytecode + zbc |
| **4** future | compiler 端 token-aware emit（不再产 string，直接 emit u32）| z42.IR / Codegen |

本 spec 仅落 Phase 1。Phase 2-4 是 follow-up。

## Scope（允许改动的文件）

### NEW

| 文件 | 说明 |
|---|---|
| `src/runtime/src/metadata/tokens.rs` | `MethodId` / `TypeId` / `BuiltinId` / `FieldId` / `StaticFieldId` / `VTableSlot` newtypes + sentinel `UNRESOLVED: u32 = u32::MAX` |
| `src/runtime/src/metadata/resolver.rs` | 加载期解析器：扫 module 所有指令，解析 string ref → token，填 `Function.resolved`；分配 StaticFieldId 全局表 |
| `src/runtime/src/corelib/dispatch_table.rs` | `BUILTINS: &[(name: &str, fn: BuiltinFn)]` 静态表 + `BuiltinId → fn` 索引 |
| `src/runtime/tests/method_token_resolution.rs` | 集成测试：cross-module / lazy load / Field IC / Static field 等场景 token 正确解析 |

### MODIFY

| 文件 | 说明 |
|---|---|
| `src/runtime/src/metadata/bytecode.rs` | `Function` 加 `resolved: OnceLock<ResolvedTokens>` 字段（`#[serde(skip)]` 默认空）；不改 Instruction struct |
| `src/runtime/src/metadata/mod.rs` | `pub mod tokens; pub mod resolver;` |
| `src/runtime/src/metadata/loader.rs` | `merge_modules` 末尾调 `resolver::resolve_module(&mut module)` |
| `src/runtime/src/metadata/types.rs::TypeDesc` | 加 `id: TypeId` 字段（默认 UNRESOLVED，resolver 分配）|
| `src/runtime/src/vm_context.rs` | `static_fields: HashMap<String, Value>` → `Vec<Value>`（按 StaticFieldId 索引）；保留 name→id 映射 `static_field_index: HashMap<String, u32>` 给 lazy load fallback |
| `src/runtime/src/interp/exec_call.rs` | `call` / `call_indirect` / `builtin` 走 token cache hot path |
| `src/runtime/src/interp/exec_object.rs` | `obj_new` 走 TypeId cache；`field_get` / `field_set` 加 FieldIC 路径；`static_get` / `static_set` 走 StaticFieldId 直接索引 |
| `src/runtime/src/interp/exec_vcall.rs` | `vcall` 走 VCallIC（mono inline cache） |
| `src/runtime/src/corelib/mod.rs` | `exec_builtin(BuiltinId, args)` 重载（接受 id），string 版本保留作 lazy resolve fallback |
| `docs/review.md` | 路线图 §VM 线 `introduce-method-token` 状态注记 |
| `docs/design/vm-architecture.md` | 加 "Method token system" 章节（按 [CLAUDE.md](../../../.claude/CLAUDE.md) 实现原理文档规则） |

### 不动（明确）

- **zbc 格式不变**：runtime 端 lazy resolve 即可，不引入 zbc 版本 bump
- **C# 编译器端不变**：仍然 emit string-based IR；runtime 自行解析
- **JIT helpers 不变**（留 Phase 2）
- **Compiler IR struct 不变**（留 Phase 4）
- **Instruction struct 字段类型不变**（留 Phase 3）

## Out of Scope

- zbc 格式 bump（Phase 3）
- JIT helper 签名变化（Phase 2 sibling spec）
- Compiler IR field 类型变化（Phase 4）
- 跨进程 / 跨版本 token 稳定性（如果有持久化）—— 当前 token 是运行时唯一性 ID，不要求跨进程稳定

## Open Questions

- [x] **Field/Static 是否纳入 Phase 1**：原 Decision 6 留 follow-up；user 2026-05-08 裁决纳入 → design Decision 6 翻转
- [x] **OnceLock vs eager resolution 选择**：design Decision 1 选 Eager（解析成本一次性分摊）
- [x] **Cross-zpkg token 的所有权**：design Decision 3 选单点回填（不全 module 重 resolve）
- [x] **Builtin 全局表与多 VM 实例**：design Decision 5 选全局静态（无状态分发，多 VM 共享安全）
- [ ] **Static field 跨 zpkg 协调**：当 zpkg A 的 `__static_init__` 在 zpkg B 之前加载，B 中 StaticGet 引用 A 的字段如何分配 ID？预案: ID 分配延后到 Vm::run 入口前的"link 阶段"——所有声明的 zpkg eager-load static_init 后统一编号
- [ ] **现有 D-1b `func_ref_cache_slots` 的关系**：本 spec 的 method_tokens 是否取代它？暂保留共存（D-1b 是 LoadFnCached 专用，本 spec 是 Call/VCall 用）—— 可在 Phase 2 合并
