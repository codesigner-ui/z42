# Proposal: introduce-method-token

## Why

z42 VM 当前**所有跨函数 / 跨类型引用都用 String** 作为身份（review.md Part 4 §4.1）：

| IR 指令 | String 字段 | 当前 dispatch |
|---|---|---|
| `Call` | `func: String` | `module.func_index.get(fname)` HashMap 查 |
| `CallIndirect` | (FuncRef value 携带 String) | 同上 |
| `VCall` | `method: String` | `type_desc.vtable_index.get(method)` HashMap 查 |
| `Builtin` | `name: String` | `corelib::exec_builtin` 字符串 match |
| `ObjNew` | `class_name: String` / `ctor_name: String` | `type_registry.get` + `func_index.get` |
| `FieldGet` / `FieldSet` | `field_name: String` | `type_desc.field_index.get` |
| `IsInstance` / `AsCast` | `class_name: String` | hierarchy walk by name |
| `StaticGet` / `StaticSet` | `field: String` | static_fields 字典 |
| `LoadFn` / `MkClos` | `fn_name: String` | 间接调用名查 |

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

1. **新增 newtype 标识符**（`metadata/types.rs`）:
   - `MethodId(u32)` —— per module，索引 `Module.functions: Vec<Function>`
   - `TypeId(u32)` —— per module，索引 `Module.classes: Vec<ClassDef>`
   - `BuiltinId(u32)` —— global，索引 corelib dispatch table
   - `FieldId(u32)` —— per type，索引 `TypeDesc.fields: Vec<FieldSlot>`
   - `VTableSlot(u32)` —— per type（实际已存在为 `usize`，仅 newtype 包装）

2. **加载期 token 解析**（`metadata/loader.rs`）: `merge_modules` 后扫所有 instruction，把可解析的 string ref → token，存到 parallel `Function.resolved: ResolvedTokens` 结构（OnceCell 懒初始化或 eager 二选一）

3. **dispatch hot path 用 token**（`interp/exec_*.rs` + `jit/helpers/*`）:
   - 命中 token cache → 直接 `module.functions[id.0 as usize]`，免 hash
   - Token 未解析（cross-zpkg lazy 触发 / OnceCell 未填）→ 走原 string lookup 路径，**填回 cache**

4. **JIT helper 签名升级**: 当前 helpers 接 `(name_ptr, name_len)`，改为 `(method_id: u32)` —— Phase 2 的工作。本 spec 落 Phase 1 (interp + 解析基础设施)，JIT 同源修改在 Phase 2 (sibling spec)

5. **Builtin 全局表**: corelib 启动时把所有 `__xxx_yyy` builtin 注册到 `BUILTINS: Vec<BuiltinFn>`，分配 `BuiltinId(u32)`；`Builtin` 指令的 `name: String` 在加载期 → `BuiltinId`

6. **TypeDesc / ClassDesc 字段访问 by-token**:
   - `TypeDesc.field_index` 已是 `HashMap<String, usize>` —— 保留作 string fallback
   - 新增 per-type `FieldId(u32) → field_slot: usize` 解析（其实就是 field_index 的 inversion）
   - `vtable_index` 同理

### Phase 化

| Phase | 内容 | scope |
|---|---|---|
| **1**（本 spec）| Token newtypes + 加载期解析 + interp dispatch token cache + builtin 全局表 | metadata + interp + corelib |
| **2** sibling spec | JIT helper signatures take MethodId/BuiltinId | jit/helpers |
| **3** future | zbc 格式 bump，IR struct 字段从 String 改 u32 token | bytecode + zbc |
| **4** future | compiler 端 token-aware emit（不再产 string，直接 emit u32）| z42.IR / Codegen |

本 spec 仅落 Phase 1。Phase 2-4 是 follow-up。

## Scope（允许改动的文件）

### NEW

| 文件 | 说明 |
|---|---|
| `src/runtime/src/metadata/tokens.rs` | `MethodId` / `TypeId` / `BuiltinId` / `FieldId` / `VTableSlot` newtypes + sentinel `UNRESOLVED: u32 = u32::MAX` |
| `src/runtime/src/metadata/resolver.rs` | 加载期解析器：扫 module 所有指令，解析 string ref → token，填 `Function.resolved` |
| `src/runtime/src/corelib/dispatch_table.rs` | `BUILTINS: &[(name: &str, fn: BuiltinFn)]` 静态表 + `BuiltinId → fn` 索引 |
| `src/runtime/tests/method_token_resolution.rs` | 集成测试：cross-module / lazy load / 空函数等场景 token 正确解析 |

### MODIFY

| 文件 | 说明 |
|---|---|
| `src/runtime/src/metadata/bytecode.rs` | `Function` 加 `resolved: OnceCell<ResolvedTokens>` 字段（`#[serde(skip)]` 默认空）；不改 Instruction struct |
| `src/runtime/src/metadata/mod.rs` | `pub mod tokens; pub mod resolver;` |
| `src/runtime/src/metadata/loader.rs` | `merge_modules` 末尾调 `resolver::resolve_module(&mut module)` |
| `src/runtime/src/interp/exec_call.rs` | `call` / `call_indirect` 走 token cache hot path，miss 时 fallback string lookup + 填回 |
| `src/runtime/src/interp/exec_object.rs` | `obj_new` 类型查找走 TypeId cache |
| `src/runtime/src/interp/exec_vcall.rs` | `vcall` 走 VTableSlot cache（per-type method-name → slot 已有，仅扩展 cache） |
| `src/runtime/src/corelib/mod.rs` | `exec_builtin(BuiltinId, args)` 重载（接受 id），string 版本保留作 lazy resolve fallback |
| `src/runtime/src/interp/exec_call.rs` 中 `builtin` | 走 BuiltinId hot path |
| `docs/review.md` | 路线图 §VM 线 `introduce-method-token` 状态注记 |
| `docs/design/vm-architecture.md` | 加 "Method token system" 章节（按 [CLAUDE.md](.claude/CLAUDE.md) 实现原理文档规则） |

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

- [ ] **OnceCell vs eager resolution 选择**：spec design.md Decision 1 给两选项，user 裁决一票
- [ ] **Cross-zpkg token 的所有权**：解析后 cache 在 caller 还是 callee 模块？design.md Decision 4
- [ ] **Builtin 全局表与多 VM 实例**：是否每个 VmContext 一份，还是单例？(memory feedback：VmContext 多实例隔离，所以应该 per-VmContext)
