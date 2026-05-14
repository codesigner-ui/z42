# z42 VM 内部实现原理

> **目的**：记录 Rust VM 的内部数据结构、算法、加载策略与关键设计决策。
> 让新接手者不必阅读大量源码即可理解"为什么这样设计"。
> 面向 VM 开发者，不面向 z42 语言使用者。
>
> 使用者视角请看 `docs/design/runtime/ir.md`、`docs/design/runtime/execution-model.md`。

---

## VmContext —— 运行时状态归口（2026-04-28）

宿主代码运行 VM 的标准流程：

```rust
let mut ctx = VmContext::new();
ctx.install_lazy_loader_with_deps(libs_dir, main_pool_len, declared, loaded);
let vm = Vm::new(module, ExecMode::Interp);
vm.run(&mut ctx, hint)?;
```

`VmContext` 持有：

- `static_fields: Rc<RefCell<HashMap<String, Value>>>` — 用户类 static 字段
- `pending_exception: Rc<RefCell<Option<Value>>>` — JIT extern "C" 边界异常槽位
- `lazy_loader: Rc<RefCell<Option<LazyLoader>>>` — 按需 zpkg 加载器
- `call_stack: Rc<RefCell<Vec<VmFrame>>>` — 单一统一帧栈（unify-frame-chain，2026-05-10）。每个 `VmFrame` 同时承载 `(regs ptr, env_arena ptr, func_name, file, line, column)`：GC root scanner 扫 regs+env_arena，stack-trace 读 name/file/line/col，interp `RefKind::Stack` 跨帧 deref 通过 `frame.regs`。push/pop 单点操作，物理上消除"GC 看得见但 trace 看不到"的部分帧 bug
- `heap: Box<dyn MagrGC>` — GC 子系统接口（默认 RcMagrGC 后端）
- `native_types: Rc<RefCell<HashMap<(String,String), Rc<RegisteredType>>>>` — Tier 1 native interop 注册表（spec C2，2026-04-29）
- `native_libs: Rc<RefCell<Vec<libloading::Library>>>` — 已加载的 native 库句柄，保活到 VM drop（spec C2）

API 方法都用 `&self`（内部 RefCell），调用方代码风格基本不变。多 VmContext
实例完全隔离 —— 这是 review2 §3 的设计目标兑现。详见
[`object-protocol.md`](../language/object-protocol.md)、[`interop.md`](../language/interop.md) 与 review2 §3 / §5.5 / §5.2。

**Native interop 入口**：`VmContext::register_native_type(Rc<RegisteredType>)` /
`resolve_native_type(module, name)` / `load_native_library(path)`；后者打开 `.dylib`
/`.so`/`.dll` 并调用其 `<basename>_register` 入口（约定）让 native 库通过
`z42_register_type` 把类型推入 `native_types`。Interp 入口设置 thread-local
`CURRENT_VM` 让 native callback 能找回 VM；详见 `src/runtime/src/native/exports.rs`
的 `VmGuard` RAII。

---

## VM 启动流程

```
z42vm <file>
  │
  ├── resolve_libs_dir()                       [main.rs]
  │      → $Z42_LIBS | <binary>/../libs | <cwd>/artifacts/build/libs/release
  │
  ├── 5.1b 加载 z42.core.zpkg (eager，隐式 prelude)
  │      → modules[0]
  │      → initially_loaded_zpkgs = ["z42.core.zpkg"]
  │
  ├── 5.1c 加载 user artifact (.zbc / .zpkg)
  │      → user_artifact.module + dependencies + import_namespaces
  │      → probe `<basename>.zsym` 同目录；存在且 build_id 匹配 →
  │        合并 sidecar DBUG 到 per-module funcs（详见下方 sidecar 章节）
  │
  ├── 5.1d 依赖加载策略
  │      interp: 纯懒加载（build_declared_candidates 填充 LazyLoader）
  │      jit/aot: eager 预加载所有声明依赖（同时也填 LazyLoader 以防 type/func 零碎 miss）
  │
  ├── 5.1e merge_modules → final_module
  │      + build_type_registry / verify_constraints / build_*_index
  │
  ├── install_with_deps(libs_dir, pool_len, declared, initially_loaded)
  │      [lazy_loader.rs]
  │
  └── Vm::new(final_module, default_mode).run(entry)
```

---

## Sidecar 调试符号加载（2026-05-10 split-debug-symbols）

### 加载策略：eager + 同步

`loader::load_zbc` / `load_zpkg` 加载主 artifact 完成后**立即**探测同目录 `<basename>.zsym` 并按 BLID 校验合并。设计选择：

| 选项 | 选择 | 理由 |
|------|------|------|
| eager vs lazy | **eager** | 异常路径（trace 构造）必须零 IO；startup 开销可控（典型 sidecar < 100KB） |
| sidecar 路径 | `<basename>.zsym` 同目录 | 不需要 search path 概念；CI/CD 部署单一目录即可 |
| BLID 算法 | BLAKE3-128 截 16B + payload 零填 hash | Rust/C# 生态成熟；payload 零填使 sidecar 与 main 字节同步 |
| BLID 不匹配 | warn + ignore，加载继续 | 调试符号缺失不应让程序无法启动 |

### 合并机制：直接在已加载的 `Module` 上 mutate

主 zpkg/zbc 解析返回 `Vec<(Module, namespace)>` 后立刻 mutate per-module Function：

```rust
for ((module, ns), (sym_ns, fns)) in module_pairs.iter_mut().zip(sidecar.modules) {
    for (i, fb) in fns.into_iter().enumerate() {
        if !fb.line_table.is_empty() { module.functions[i].line_table = fb.line_table; }
        if !fb.local_vars.is_empty() { module.functions[i].local_vars = fb.local_vars; }
    }
}
```

不用 `RefCell` / `OnceCell` 包装 `line_table` — sidecar merge 发生在 `merge_modules` 之前的可变所有权窗口内。这避免引入运行期 borrow check 开销。

### 与 `merge_modules` 的顺序

`load_zpkg_bytes_with_sidecar` 调用顺序：

1. `read_zpkg_modules(raw)` → `Vec<(Module, ns)>` 拥有可变 Module
2. `apply_zpkg_sidecar(&mut module_pairs, raw, sym, sym_path)` — 把 sidecar DBUG 合入 per-module funcs
3. `merge_modules(modules)` — 命名空间扁平化 + 函数表合并
4. `build_type_registry` / `verify_constraints` / `build_func_index`

为什么 sidecar 在 merge 之前：sidecar 的 MDBG section 按 module namespace 索引，merge 后 namespace 信息丢失到扁平化 IR；提前 merge 会让 sidecar 的"按 ns 配对"丢失对应关系。

### 容错路径

| 情况 | 行为 |
|------|------|
| sidecar 文件不存在 | 静默继续；trace 退化为 `at <FQN>(<sig>)` |
| sidecar 文件 magic 错 / 缺 BLID | `tracing::warn` + 忽略，加载继续 |
| BLID 不匹配 | `tracing::warn` 显示 `main=<8hex>` / `sidecar=<8hex>` + 忽略 |
| sidecar module 数 ≠ main module 数 | warn + 忽略 |
| 单个 module 内 function 数不一致 | warn + 跳过该 module，其他 module 仍合并 |
| Module 加载主 zbc / zpkg 被发现 `SymOnly` flag | `bail!` —— sidecar 不可作为主模块加载 |

> **实现位置**：[src/runtime/src/metadata/loader.rs](../../../src/runtime/src/metadata/loader.rs) `apply_zbc_sidecar` / `apply_zpkg_sidecar`；解析在 [zbc_reader.rs](../../../src/runtime/src/metadata/zbc_reader.rs) `parse_zbc_sidecar` / `parse_zpkg_sidecar`。

## Embedding Entry（2026-05-10 add-embedding-api H1）

> **本节边界**：从 VM 内部视角描述 `crate::host` 模块如何融入 VM 架构（数据归属、状态管理、与 `crate::native` 的边界）。**API 形态、Host C ABI 函数签名、宿主使用模式**归 [`embedding.md`](embedding.md)，不在本文重复。

`src/runtime/src/host/` 是 z42 VM 的**宿主嵌入入口**：与上方 `z42vm` CLI 启动流程并列，存在于另一条进入 VM 的路径。

```
host application (iOS / Android / IDE 插件 / 其他 native)
   │
   │ z42_host_initialize(&cfg, &handle)
   ▼
host::state::HOST  (RwLock<Option<HostState>>，进程单实例)
   │
   │ z42_host_load_zbc(handle, bytes, len, &mod)        [H2 待实施]
   │ z42_host_resolve_entry(handle, mod, fqn, &entry)   [H2 待实施]
   │ z42_host_invoke(entry, args, n, &result)           [H2 待实施]
   ▼
（H2）interp::run_method / jit::run（与 CLI 启动流程汇合到同一执行路径）
```

### 模块边界与 VM 全局状态

| 关系 | H1 状态 | H2 计划 |
|------|---------|---------|
| host 模块 ↔ `VmContext` | host 模块**未触及**任何 VM 全局状态；只在 `host::state::HOST` 持有 `ResolvedConfig` | `load_zbc` 接 `metadata::ZbcReader` + `merge_modules`；`invoke` 走 `interp::run_method` |
| stdout / stderr | `Z42WriteSink` 函数指针存在 `HostState`，未注入 VM stdout writer | sink 包装为 `impl Write`，在 `invoke` 启动前 swap VM stdout writer，结束 / shutdown 时复原 |
| panic 隔离 | 每个 `extern "C"` 入口经 `host::guard()` `catch_unwind` 兜底，panic → `Z42_HOST_ERR_INTERNAL` | 不变 |
| 错误诊断 | TLS `LAST_ERROR` 由 `host::error::set_error / clear_error` 管理；与 `native::error::LAST_ERROR` 独立（两条 ABI 各自维护） | 不变 |

### 与 `crate::native` 的边界

| 方向 | 模块 | 解决问题 |
|------|------|---------|
| native 代码 → 注册类型/方法 | `crate::native` (`z42_register_type` 等) | 扩展语言（CPython C 扩展类比） |
| 宿主 app → 启动 VM | `crate::host` (`z42_host_initialize` 等) | 嵌入运行时（CoreCLR `coreclrhost.h` 类比） |

两者复用 `z42_abi::Z42Value` / `Z42Args` / `Z42Error` 类型，互不调用对方。

### 单实例 vs 多实例

v0.1 单实例：`HOST: RwLock<Option<HostState>>`。`Z42HostRef` 是一个 sentinel pointer（`0x1`），所有 host API 调用读 `HOST` 验证活跃。

多实例 / ALC-like 上下文进 [embedding.md §12 Deferred](embedding.md)。届时 `RwLock<Option<...>>` 升级为 `Slab<HostState>`，`Z42HostRef` 编码 `(idx, gen)`，VM 全局状态（GC heap、JIT cache、type registry）必须 per-handle 化。

详见 [docs/design/runtime/embedding.md](embedding.md) 与 [docs/spec/archive/2026-05-10-add-embedding-api/design.md](../../spec/archive/2026-05-10-add-embedding-api/design.md) D1/D5。

---

## LazyLoader：zpkg-based 依赖懒加载（2026-04-25 重构）

### 为什么改成 zpkg-based

**旧模型（namespace-based）**：Call miss 时从 FQ func_name 提取 namespace
前缀，调 `resolve_namespace` 在 libs 目录找**唯一**一个声明该 namespace 的
zpkg。若 ≥2 个 zpkg 共享同 namespace → `bail!("AmbiguousNamespaceError")`。

**为什么不行**：C# BCL 对齐后（stdlib 重组 W1），`Std.Collections` namespace 同
时出现在 `z42.core.zpkg`（List / Dictionary）和 `z42.collections.zpkg`
（Queue / Stack）。旧模型拒绝这种合法情况。

**新模型（zpkg-based）**：对齐 C# CLR 的 AssemblyRef 模型 —— **zpkg 是加载
单位，namespace 是逻辑分组**，多 zpkg 可共享同 namespace。

### 核心数据结构

```rust
struct LazyLoader {
    libs_dir: Option<PathBuf>,
    main_pool_len: usize,          // 主模块 string pool 长度（索引偏移基准）
    string_pool: Vec<String>,       // 聚合懒加载 string pool

    loaded_zpkgs: HashSet<String>,                  // 已加载 zpkg 文件名
    declared_zpkgs: HashMap<String, ZpkgCandidate>, // 声明但未加载

    function_table: HashMap<String, Arc<Function>>, // FQ name → Function
    type_registry: HashMap<String, Arc<TypeDesc>>,  // FQ name → TypeDesc
}

struct ZpkgCandidate {
    file_path: PathBuf,
    namespaces: Vec<String>,  // 该 zpkg 导出的 namespace 列表（从 NSPC section 读）
}
```

### Call miss 触发策略（Decision 1：策略 C + 回退 B）

```
try_lookup_function(func_name):
  if function_table has func_name → return hit

  # 策略 C：精确路由 —— 按 namespace 前缀筛选候选 zpkg
  ns = namespace_prefix(func_name)   // e.g. "Std.Collections.Stack.Push" → "Std.Collections"
  for zpkg_file in declared_zpkgs:
    if zpkg_file not in loaded_zpkgs
       and zpkg.namespaces 含 ns 或以 ns. 开头:
      load_zpkg_file(zpkg_file)
      if function_table has func_name → return hit

  # 策略 B 回退：若策略 C 无匹配，遍历所有剩余 declared-but-not-loaded
  for zpkg_file in declared_zpkgs - loaded_zpkgs:
    load_zpkg_file(zpkg_file)
    if function_table has func_name → return hit

  return None  # 真正 undefined
```

策略 C 的精确路由等价于 C# CLR 的 TypeRef → TypeDef 查找：按 namespace
（assembly 的 public type 的父 namespace）作为高效过滤器。

策略 B 是安全网，处理 zpkg 元数据不完整 / 用户 zbc 的 import_namespaces
不全等边界情况。

### 依赖传递（Decision 4：着色算法）

```
load_zpkg_file(file_name):
  if file_name in loaded_zpkgs → return    # 已加载/正在加载（防 re-entry）
  loaded_zpkgs.insert(file_name)           # ★ 着色：先标记后加载

  artifact = load_artifact(file_path)
  remap ConstStr indices + 合并 function_table / type_registry（first-wins）

  # 递归展开该 zpkg 自己的 ZpkgDep 进 declared 集合
  for dep in artifact.dependencies:
    if dep.file not in loaded_zpkgs and not in declared_zpkgs:
      declared_zpkgs[dep.file] = ZpkgCandidate::build(libs_dir, dep.file)
```

**为什么先着色后加载**：若 A 依赖 B、B 又依赖 A（或自依赖），加载 B 时
`load_zpkg_file("a.zpkg")` 立刻返回（因 A 已在 loaded 集合），避免无限递归。

### 函数 / 类型冲突（Decision 6：first-wins）

合并 function_table / type_registry 时若遇同名 entry：保留先加载者 +
`tracing::warn`。预期同名冲突不应发生（编译期类型检查捕获）；若发生，稳定
的行为（不随加载顺序变化）比"最后加载者覆盖"更安全，和 C# CLR 一致。

### ConstStr 索引重映射

主模块 string pool 的索引域是 `[0, main_pool_len)`；懒加载 zpkg 的
ConstStr 原始索引是相对自己 pool 的。为统一，合并时：

```rust
offset = main_pool_len + self.string_pool.len()
// 新加载 zpkg 的每个 Function 的 ConstStr.idx += offset
self.string_pool.extend(artifact.module.string_pool)
```

运行时 `try_lookup_string(absolute_idx)` 返回：
- `idx < main_pool_len` → 主模块 pool
- `idx ≥ main_pool_len` → 懒加载 pool[idx - main_pool_len]

### `z42.core` 永不经过懒加载

`z42.core.zpkg` 在 main.rs 5.1b 阶段就被 eager 加载并 merge 进 main
module。`initially_loaded_zpkgs` 含 `"z42.core.zpkg"`，所以 LazyLoader
的 `declared_zpkgs` 根本不含它。这保证 prelude 语义（所有 `Std.*` 符号
启动即可用）。

### 两阶段类型加载：cross-zpkg subclass 字段继承（2026-05-14 fix-cross-pkg-subclass-fields）

**问题**：`build_type_registry` 在每个 module 加载时**单独运行**，只能
基于本 module 的 `type_registry` 解析 base 类。当 z42.io 中的 `class
Sub : Std.Exception { ... }` 加载时，`Std.Exception`（在 z42.core）尚
不在 z42.io 的本地 registry —— `registry.get("Std.Exception")` 返回
`None`，继承的字段 / vtable 槽位丢失。`Sub.fields` 只含自己声明的字段，
`field.set @Message` 触不到正确的 slot，`Message` 永远是 null。

**解决方案**：两阶段（skeleton + fixup）类型加载。

**阶段 1 — skeleton**（`build_type_registry`）：
- 计算 **own_fields / own_methods**（本类自己声明的字段 / 方法），保存
  到 TypeDesc。这部分不依赖外部信息，是稳定的。
- 用 `merge_with_base` 计算**初始** `fields / field_index / vtable /
  vtable_index`。本 module 内 base 可解析 → 完整继承；跨 zpkg base 不
  可解析 → 仅含 own 部分（后续 fixup 补齐）。

**阶段 2 — fixup**（`try_fixup_inheritance`）：
- 在 [`LazyLoader::load_zpkg_file`](../../src/runtime/src/metadata/lazy_loader.rs)
  把新 zpkg 的 TypeDesc 插入全局 `type_registry` 之后调用。
- 扫描整个 `type_registry`，对每个 base 链 *新近可解析* 的 TypeDesc
  （`needs_fixup` 检测），用 `merge_with_base` 用全局 registry 重算
  layout，然后 `Arc::get_mut` mutate 该 TypeDesc。
- 固定点循环（`while try_fixup_inheritance() > 0`）—— 一次 fix-up 可能
  让另一个 TypeDesc 变可解析（三级链 `A → B → C`：B 先 fix → C 后 fix）。

**`Arc::get_mut` 可行性**：lazy_loader 是当前 TypeDesc 的**唯一**强引
用持有者（type 还没被任何对象实例化）。`build_type_registry` 之后立刻
做 `module.type_registry_vec.clear()` 释放 by-id Vec 的 Arc 副本，
确保 fixup 时 strong_count = 1。

**Eager-loaded 类型的可见性**：merge 后的 main module（含 z42.core）
的 TypeDesc 通过 `VmContext::seed_lazy_loader_types(&final_module.type_registry)`
克隆到 LazyLoader 的 `type_registry`。这些 Arc strong_count = 2（main
+ lazy_loader）—— 它们不需要 mutate，因为 `build_type_registry` 已正
确解析了它们的 base（base 在同一 merged module 内）。

**`needs_fixup` 计算**：
```text
expected_field_count  = base.fields.len() + len(unique own_fields not in base)
expected_vtable_count = base.vtable.len() + len(distinct simple_names of
                        own_methods not already in base.vtable_index)
needs_fixup = td.fields.len() != expected || td.vtable.len() != expected_v
```

**幂等性**：fixup 运行两次产生相同结果。已正确的 TypeDesc（`needs_fixup`
返回 false）跳过；BLOCKED（strong_count > 1）也不算 newly_fixed → 收
敛到 fixed point。

**延后未解析**：base 一直不可解析时 → 不报错，TypeDesc 保持 own-only
状态。下次新 zpkg 加载触发的 fixup 会重试。VM 退出时若仍有 own-only
的子类，相关字段访问会写入越界 slot（潜在 UB）—— 当前依赖 build 系统
保证依赖完整性；未来可在 try_lookup_type 路径加 "all-deps-loaded" 断言。

**前置 spec**：[docs/spec/archive/2026-05-14-fix-cross-pkg-subclass-fields/](../../spec/archive/2026-05-14-fix-cross-pkg-subclass-fields/)
（首次触发：add-std-process 的 4 个 z42.io exception 类继承 z42.core
的 Std.Exception）。

---

## resolve_namespace / resolve_dependency 的分工

位置：`src/runtime/src/metadata/loader.rs`

| 函数 | 签名 | 语义 | 调用方 |
|------|------|------|--------|
| `resolve_namespace(ns, module_paths, libs_paths)` | `-> Result<Vec<PathBuf>>` | 返回**所有**声明该 namespace 的 zbc/zpkg 文件（不再 bail on ambiguous）| 编译期诊断 / main.rs 启动反查 (.zbc → zpkg) |
| `resolve_dependency(zpkg_file, libs_paths)` | `-> Result<Option<PathBuf>>` | 按 zpkg 文件名直接查找 | LazyLoader 内部（按文件名精确定位）|

**设计权衡（Decision 2）**：`resolve_namespace` 保留但改语义，而非彻底删除。
理由：编译器 / 诊断工具仍可能问"哪些 zpkg 提供某 namespace"，保留此 API
更通用。

---

## VCall 分发与 TypeDesc

### ObjNew dispatch（与 VCall 对称）

`Instruction::ObjNew { dst, class_name, ctor_name, args }`：

```
1. type_desc = module.type_registry[class_name] | lazy_loader.try_lookup_type(class_name)
            | make_fallback_type_desc(...)
2. allocate ScriptObject(type_desc, slots=fields.iter().map(|f| default_value_for(&f.type_tag)).collect())
3. ctor_fn = module.func_index[ctor_name] | lazy_loader.try_lookup_function(ctor_name)
4. if ctor_fn: exec_function(ctor_fn, [obj, ...args])
   else:       skip ctor call（默认无参 ctor 语义；TypeChecker 已确保
               有显式 ctor 时 ctor_name 命中）
5. frame[dst] = obj
```

**与 VCall 对齐的核心**：编译期完整 overload resolution，VM 直查 `ctor_name`，
不再做 `${class}.${simple}` 名字推断（2026-04-26 add-objnew-ctor-name 重构）。
`ctor_name` 含 `$N` arity suffix（重载场景）；单 ctor 时无 suffix。

**字段默认值（2026-05-02 fix-class-field-default-init）**：步骤 2 把
slot 初始化为对应类型的默认值（`int*`/`f64*` → 0、`bool` → false、`char` →
`'\0'`、`str` / 引用 → null），不再一律 `Null`。具体映射由
`metadata::default_value_for(type_tag) -> Value` 单一函数提供，interp 与
JIT (`jit_obj_new`) 共享实现。需要这一步的前提是 `FieldSlot` 携带
`type_tag: String`（从 zbc `FieldDesc.type_tag` 透传），见下文 TypeDesc 结构。

ctor 入口由编译器侧 IrGen 注入字段 init（base ctor call 之后、用户 body
之前）；无显式 ctor 但本类或本地祖先链有字段 init 的类，编译器合成无参
隐式 ctor 内联整条链的 init 表达式。详见
`docs/design/language/language-overview.md` §6.3 + `docs/spec/archive/2026-05-02-fix-class-field-default-init/`。

### TypeDesc 结构

```rust
struct TypeDesc {
    name: String,                          // FQ 类名，e.g. "Std.Collections.Stack"
    base_name: Option<String>,             // 父类 FQ 名
    fields: Vec<FieldSlot>,                // 布局顺序，基类字段在前
    //   FieldSlot { name: String, type_tag: String }
    //   type_tag 用于 ObjNew 选默认值（fix-class-field-default-init）
    field_index: HashMap<String, usize>,   // O(1) 字段名 → slot
    vtable: Vec<(method_name, func_name)>, // 方法名 → FQ func name
    vtable_index: HashMap<String, usize>,  // O(1) method → vtable slot
    type_params: Vec<String>,              // 泛型类型参数（L3-G 起）
    type_args: Vec<String>,                // 实例化时具体类型（运行时填）
    type_param_constraints: ...,            // L3-G3a 约束元数据
}
```

TypeDesc 由 `build_type_registry`（loader.rs）在模块加载完成后按 topo
sort 预计算一次，避免运行时重复构建。

### VCall 指令执行

位置：`src/runtime/src/interp/exec_instr.rs:416`

```rust
let type_desc = obj.type_desc.clone();

// 1. 优先走预计算 vtable
let func_name = if let Some(&slot) = type_desc.vtable_index.get(method) {
    type_desc.vtable[slot].1.clone()
}
// 2. fallback：沿继承链查（基类可能没在 vtable 里）
else if let Ok(f) = resolve_virtual(module, &type_desc.name, method) {
    f.name.clone()
}
// 3. 最后兜底：直接 format!("{}.{}", type_desc.name, method)
else {
    format!("{}.{}", type_desc.name, method)
};

// 查 main module func_index → fallback lazy_loader
```

**关键不变量**：`type_desc.name` 必须是 FQ 名（否则 fallback 3 生成的
func_name 是 bare 名 → miss）。`build_type_registry` 从 `Module.classes[].name`
构建 TypeDesc.name，而 ClassDesc.name 由编译器写入 —— 编译期 `QualifyClassName`
是最终决定者。

---

## 闭包 dispatch（Closure / StackClosure / FuncRef）

`Value` 三种 callable variant 的 CallIndirect 路径：

```
match callee_value {
  FuncRef(name)               → 直接 call name；无 env
  Closure { env, fn_name }    → 把 env (GcRef) 作 implicit first arg → call fn_name
  StackClosure { env_idx, fn_name } → 从 caller frame.env_arena[env_idx] clone Vec<Value>
                                       → 升格为临时 GcRef → 作 implicit first arg → call fn_name
}
```

**StackClosure**（2026-05-02 impl-closure-l3-escape-stack）：env 在 caller frame 的 `env_arena: Vec<Vec<Value>>` 中，零堆分配。CallIndirect 时复制内容到临时 GcRef，callee 不区分 stack/heap 来源。

**GC root**：每个 `VmFrame` 内嵌 `regs` + `env_arena` 指针（unify-frame-chain），GC scanner 单循环遍历 `call_stack` 即可同时 mark frame regs 和 stack closure env 中的 Object/Array refs，确保不被回收。

**lifetime 安全**：StackClosure value 的有效性由分析器（`ClosureEscapeAnalyzer`）在编译期保证 —— closure value 永不离开创建它的 frame；CallIndirect 复制 env 内容也意味着 callee 不会持有指向 caller arena 的悬空指针。

## interp vs JIT 分发

位置：`src/runtime/src/vm.rs` + `interp/` + `jit/`

- **interp**：`exec_function` → `exec_instr` match 分发到 ~60 个 IR 指令
  实现。`Value` 是 `Rc<RefCell<ValueKind>>` 或 enum 变体
- **JIT**：Cranelift 后端编译每个 `Function` 成 native code。`ExecMode`
  注解决定 function-level 的分发路径；模块默认模式由 `Vm::default_mode`
- **AOT**：留白（interp 全绿前不填实现）

两种后端共享：
- `Module` / `Function` / `Instruction` 数据结构
- `TypeDesc` / `FieldSlot` / vtable
- corelib builtin 调度表（`corelib/dispatch.rs`）
- LazyLoader（但 JIT 走 eager 预加载路径，基本不触发 lazy load）

---

## JIT/EE helper 边界（2026-05-07 formalize-jit-vm-interface）

位置：`src/runtime/src/jit/helpers/`

**问题**: JIT-compiled native code 需要回调 VM 完成所有非纯算术操作（构造对象、字符串、调度虚方法、读静态字段、抛/捕异常等）。这些回调被称为 "helper"，每个是一个 `#[no_mangle] pub unsafe extern "C" fn jit_xxx(...)`。Cranelift 通过 symbol-by-name 解析（`JITBuilder::symbol(name, ptr)`）让生成的 native code 调到 helper。

**要求**: 单一改动点。加新 helper 不能让人在 3 个文件里同步签名（容易漂移）。

**结构**: 三个职责文件 + 按 `Instruction` 类别拆的 helper 子文件:

```
jit/helpers/
├── mod.rs        — 共享工具（vm_ctx_ref / set_exception / 数值 helper）+
│                   VM_JIT_INTERFACE_VERSION
├── registry.rs   — 中央注册表（单一真相源）
│                   ├── HelperIds {fields...}     ← 一字段一 FuncId
│                   ├── register_symbols(builder) ← 给 JITBuilder 绑名→指针
│                   └── declare_imports(jit) -> HelperIds
│                                                 ← 给 JITModule 声明签名
├── value.rs      — Const* / Copy / 字符串 / get_bool / set_ret
├── arith.rs      — 算术 / 比较 / 逻辑 / 一元 / 位运算
├── control.rs    — throw / install_catch / match_catch_type
├── call.rs       — jit_call / jit_builtin
├── array.rs      — Array*
├── object.rs     — ObjNew / Field* / IsInstance / AsCast / Static* / default_of
├── vcall.rs      — 虚调用（独立文件，含 primitive-as-struct + 懒加载 fallback）
└── closure.rs    — load_fn / load_fn_cached / mk_clos / call_indirect
```

**与 `interp/exec_*.rs` 命名对称**: 加新 IR 指令时，interp 与 JIT 改的文件名一一对应（`exec_value.rs` ↔ `helpers/value.rs`），认知负担最小。

**消费者**: 
- `jit/mod.rs::compile_module` 调 `helpers::register_symbols(&mut builder)` 一次
- `jit/translate.rs` 通过 `pub use super::helpers::HelperIds;` 重导，并消费 `helpers::declare_imports(&mut jit)` 拿 `HelperIds`

**新增 helper 改 2 处**（不再是 3 处）:
1. `helpers/<category>.rs` 添加 `pub unsafe extern "C" fn jit_xxx(...)` 函数体
2. `helpers/registry.rs` 添加:
   - `HelperIds.xxx: FuncId` 字段
   - `register_symbols` 中 `reg!("jit_xxx", category::jit_xxx);` 行
   - `declare_imports` 返回的 `HelperIds {...}` 中 `xxx: decl!("jit_xxx", [params...], [returns...]);` 行

`mod.rs` 与 `translate.rs` 都不再需要知道 helper 列表。

**`VM_JIT_INTERFACE_VERSION: u32 = 1` 作为 hook**: 当前没有运行时校验消费者——单一 JITModule 实现，bump 这个常量当 helper 集合或签名变化时即可。未来若引入第二个 JIT 后端（LLVM / wasm）或多版本 tier-up，启动时校验该值与编译期版本是否兼容，避免 cross-version helper 错配。属于 review.md Part 4 §4.2 提出的"边界形式化"目标，**不引入校验代码避免过度设计**，留 hook 即可。

**为什么不引入 trait**: trait object dispatch 会带来间接调用开销，违背 zero-cost helper 设计；且 helper 已经走 cranelift symbol 解析机制（按名解析），用 trait 反而绕路。保留 `extern "C"` 直调零开销，仅在边界**形态**上做形式化（HelperIds + registry），不动**调用机制**。

**与 CoreCLR `ICorJitInfo` 对照**: CoreCLR 的 JIT/EE 边界是 callback-based vtable（`ICorJitInfo` 含 ~100 callback），versioned via GUID。z42 当前 ~50 helper、单实现、symbol-by-name 解析——比 ICorJitInfo 简洁但同方向（"单一边界 + 版本号"）。等需要支持多 JIT 后端时再考虑升级到 vtable 形态。

---

## Method token system（2026-05-08 introduce-method-token Phase 1）

位置：`src/runtime/src/metadata/tokens.rs` + `metadata/resolver.rs` + `vm_context.rs::resolve_static_field_id` + 各 `interp/exec_*.rs` 热路径

**问题**: review.md Part 4 §4.1 痛点 — z42 IR 所有跨引用 dispatch 用 `String` + `HashMap.get()` 做身份。每次虚调用一次 hash + 字符串等价比较；IR 内存表示膨胀；反射 R-series 设计无 token 锚点。

**设计**: 加载期解析所有可解析的 string 引用为 newtype token (`MethodId(u32)` / `TypeId(u32)` / `BuiltinId(u32)` / `FieldId(u32)` / `StaticFieldId(u32)` / `VTableSlot(u32)`)，存到外置 `Function.resolved: OnceLock<ResolvedTokens>`，热路径直接 Vec/Array 索引。无需改 IR `Instruction` struct 字段类型（zbc 格式不动；compiler 端不变）。

### 数据结构

```rust
// metadata/tokens.rs — 6 个 newtype + UNRESOLVED sentinel
pub const UNRESOLVED: u32 = u32::MAX;
pub struct MethodId(pub u32);     // → Module.functions[id]
pub struct TypeId(pub u32);       // → Module.classes order
pub struct BuiltinId(pub u32);    // → BUILTINS[id] 全局静态表
pub struct FieldId(pub u32);      // → TypeDesc.fields[id]
pub struct StaticFieldId(pub u32);// → VmContext.static_fields[id]
pub struct VTableSlot(pub u32);   // → TypeDesc.vtable[id]

// metadata/resolver.rs — Function.resolved 内容
pub struct ResolvedTokens {
    pub method_tokens:        Vec<AtomicU32>,   // Call 站点（cross-zpkg 留 UNRESOLVED）
    pub builtin_tokens:       Vec<u32>,         // Builtin 站点（100% 命中）
    pub type_tokens:          Vec<AtomicU32>,   // ObjNew 站点
    pub vcall_ic:             Vec<VCallIC>,     // VCall 单态 IC
    pub field_ic:             Vec<FieldIC>,     // FieldGet/Set 单态 IC
    pub static_field_tokens:  Vec<AtomicU32>,   // StaticGet/Set 站点
    pub site_index:           Vec<Vec<u32>>,    // (block, instr) → per-kind site_idx
}
```

### 解析时序

1. **`metadata::loader::build_type_registry`**: 在 topo order 中给每个 `TypeDesc.id` 分配（0..N）
2. **`vm.rs::Vm::run`**: 调 `resolver::resolve_module(&module, ctx)`：
   - 走每个 Function 的每个 (block, instr) 元组
   - 对每个 token-bearing instruction 分配 per-kind site_idx
   - 解析能解析的 token：
     - `Call.func` → `module.func_index` 命中 → `MethodId`，否则 `UNRESOLVED`
     - `Builtin.name` → `corelib::builtin_id_of` 命中 → `BuiltinId`（必须，否则 panic）
     - `ObjNew.class_name` → `module.type_registry` → `TypeId`
     - `StaticGet/Set.field` → `ctx.resolve_static_field_id(name)` 懒分配
     - `VCall` / `FieldGet/Set` → 留 IC UNRESOLVED（receiver-type-dependent）
   - `function.resolved.set(...)` (OnceLock idempotent)

### 热路径行为

每条 token-bearing 指令在 `interp::exec_instr` 入口查 `resolved.site_index[block_idx][instr_idx] → site_idx`，传给对应 helper：

- **Call**: 命中 → `module.functions[cached]`；UNRESOLVED → `func_index` 查找 + 写回 cache（cross-zpkg 单点回填）
- **Builtin**: 直接 `BUILTINS[id]`（无 fallback；100% 命中）
- **ObjNew**: 仍走 `type_registry`（HashMap by name）；TypeId cache 用作 cross-zpkg observability
- **StaticGet/Set**: 命中 → `static_fields[id]`；UNRESOLVED → name lookup + 回填
- **VCall**: 单态 IC 命中（`recv.type_desc.id == cached_type_id`）→ 直调 `module.functions[cached_fn_idx]`；miss → 走原 4 段 dispatch + 在 vtable_index hit 时填 IC
- **FieldGet/Set**: 单态 IC 命中 → 直读/写 `obj.slots[cached_slot]`；miss → `field_index` 查 + 填 IC

### 跨 zpkg 时序

- **Intra-module**：`build_type_registry` + `resolve_module` 都在同一 module 加载完成后跑，所有 intra-module ID 立即可用。
- **Cross-zpkg lazy load**：lazy_loader 触发的 zpkg 加载后，对方模块的 `func_index` / `type_registry` 才填充。caller 模块的 `Function.resolved` 中的 cross-zpkg 引用初始为 `UNRESOLVED`；首次 dispatch 通过 string lookup 命中后**写回 cache**，单点回填。
- **StaticFieldId 全局 lazy 增量**：`VmContext::resolve_static_field_id(name)` idempotent — 任何模块在加载期或 dispatch 期遇到新的 static field name，立即分配新 id 并 resize Vec。已分配的 id 不变（resolver-populated cache 跨 module reload 仍有效）。

### Phase 化路线

| Phase | 内容 | 状态 |
|---|---|---|
| 1 | Token newtypes + 加载期 resolver + interp 8 热路径 + StaticField 全局编号 + Field/VCall 单态 IC | 🟢 2026-05-08 |
| 2 (sibling) | JIT helpers 接 MethodId/BuiltinId/StaticFieldId + VCall/Field IC（Option A — helper carries IC） | 🟢 2026-05-08 |
| 3 | zbc 1.0 wire format（IR 字段 token 化：本地 = `module.Functions/Classes` 索引；cross-zpkg = `IMPORT_BASE + STRS idx`）+ type_registry Vec-by-TypeId（吸收 Phase 2.D） | 🟢 2026-05-09 |
| 4 (future) | Compiler 端 token-aware emit perf；Phase 2.E Option B（JIT 机器码 inline IC check）作 perf 工作 | 📋 待启动 |

> **Phase 2 边界**: Phase 2 已把 JIT helper 切到 token / IC 形态，但 dispatch
> 仍走 helper-call 一次（Option A）。机器码 inline IC check（Option B，跳过
> helper 调用本身）需要 cranelift 端的复杂 control-flow，留给 Phase 4 perf
> 阶段。Option A 已让 JIT 的 hot path 与 interp 行为完全对齐：IC hit 时直接
> 通过 `JitModuleCtx.fn_entries_by_id[cached_fn_idx]` 跳到目标 native 代码，
> 无 HashMap 哈希、无 vtable 解析。
>
> **Phase 2.D 推迟**: ObjNew TypeId 在 Phase 2 阶段是纯 observability（type_registry
> 仍是 HashMap-by-name，dispatch 路径没有热路径键可换）；与其单独写一遍 helper
> 签名变更，不如等 Phase 3 把 type_registry 改 Vec-by-TypeId 时一起切。
>
> **Phase 3 实施期决策（2026-05-09 redesign）**：原 Phase 3 设计要求 IMPT 区段
> 扩展 + sort coordination + Rust Instruction enum 字段改 newtype。实施期发现
> 三件复杂度叠加导致无法 GREEN（commit `833193a` 在 `wip/phase3-s3-broken` 分支
> 保留作为反例）。重设计取消上述三项，改用：(1) STRS 池复用 + IMPORT_BASE bit
> 编码 cross-zpkg 引用（不需新 IMPT 格式）；(2) 源序分配 token id（不需 sort
> coordination）；(3) IR enum 字段保持 `String`，token 化只在 wire 边界发生。
> 三步骤 (S3a/b/c) 每步独立 GREEN：S3a 让 Rust reader 双版本支持，S3b 切换
> 编译器 + stdlib regen，S3c 清理 v0.x fallback。

### 与 D-1b `func_ref_cache_slots` 的关系

`func_ref_cache_slots`（D-1b add-method-group-conversion）是 method group 转换的 module-level 缓存，与本 spec 的 `method_tokens` 是两套独立的运行时 cache。Phase 2（JIT 端）若做 method-token 整合时，可考虑统一到一套 token 系统。

---

## corelib builtin dispatch

位置：`src/runtime/src/corelib/`

`Builtin` IR 指令在 interp / JIT 都走 `corelib::exec_builtin(name, args)` 统一入口。
builtin 按功能分 submodule：`string.rs` / `io.rs` / `math.rs` / `collections.rs` 等。

**新增 builtin 三处同改（强制规则）**：
1. `corelib/<module>.rs` — 实现
2. `corelib/dispatch.rs` — match 分发
3. `src/compiler/z42.Compiler/TypeCheck/BuiltinTable.cs` — 类型签名

---

## 关键设计权衡

### 为什么 Value 用 Rc<RefCell>

- **GC 模型**：z42 是 GC 语言；`Rc<RefCell>` 给每个对象引用计数 + 运行时
  borrow check
- **已知限制**：`Rc` 是 `!Send`，阻塞跨线程传值。L3 async / 线程模型时会
  重新设计（L2 backlog A6）

### 为什么懒加载现在归 VmContext 持有（2026-04-28 consolidate-vm-state）

**之前**：`lazy_loader::STATE` thread_local + `install` / `uninstall` /
`try_lookup_*` 自由函数集合。简单但带来的问题：

- 同一进程跑多个 VM 实例时 STATE 互串（多 VM-per-process 卖点失效）
- 测试间状态污染靠手动 `uninstall`，遗漏即漂移
- 与 README 第 7 行 "Multi-threaded: GC-safe concurrency" 设计目标矛盾

**现在**：`VmContext.lazy_loader: RefCell<Option<LazyLoader>>` 持有，
`ctx.install_lazy_loader_with_deps(...)` / `ctx.try_lookup_function(name)` /
`ctx.declared_namespaces()` 等方法委托到 `LazyLoader` struct（保留逻辑不变）。
两个 ctx 实例的 lazy_loader 完全隔离，跨 ctx 切换不污染。

同时迁移到 VmContext 的还有：

| 状态 | 之前 | 现在 |
|------|------|------|
| 用户类静态字段 (interp) | `interp/dispatch.rs::STATIC_FIELDS` thread_local | `ctx.static_fields` |
| Pending exception (interp) | `interp/mod.rs::PENDING_EXCEPTION` thread_local + `UserException` sentinel + `user_throw`/`user_exception_take` | 删除 — interp 全程走 `ExecOutcome::Thrown(Value)` |
| Pending exception (JIT) | `jit/helpers.rs::PENDING_EXCEPTION` thread_local | thread_local 保留（extern "C" ABI）；`JitModule::run` 在边界与 `ctx.pending_exception` sync |
| 静态字段 (JIT) | `jit/helpers.rs::STATIC_FIELDS` thread_local | thread_local 保留；同样 sync 机制 |

> 2026-04-28 update（extend-jit-helper-abi）：JIT 端的 2 个 thread_local
> 已删除。所有 ~37 个 extern "C" helper 签名都加了 `ctx: *const JitModuleCtx`
> 第 2 参（含 macro 实例化的批量 helper），Cranelift translate.rs 同步在
> 调用点插入 `ctx_val`。helper 内部通过 `vm_ctx_ref(ctx)` → `(*ctx).vm_ctx`
> 两层间接拿到 `VmContext`，不再有任何同步桥接代码。
>
> Runtime 内仅余 `jit/frame.rs::FRAME_POOL`（pure allocator cache，每线程
> 独立池子合理）。`VmContext` 是所有 runtime-mutable 状态的唯一规范来源。

### 为什么不预加载所有 stdlib

User 裁决（2026-04-25）：保留懒加载，按需加载。原因：
- 启动更快（stdlib 可能扩到几十个 zpkg，一次全加载浪费）
- lazy_loader 状态机虽然复杂，但代码已存在，维护成本可接受
- 失败模式一致：miss 时才真正报错，符合语言用户直觉

### 为什么 ConstStr 要重映射索引

主模块的 IR 在编译期生成时已经基于其 string pool；懒加载的 Function 的
ConstStr 索引相对自己 pool。合并时若不重映射，懒加载函数里的 `ConstStr(3)`
会引用主模块 pool[3] 而不是它自己的 pool[3]。`remap_const_str` 加一个
`offset` 把懒加载索引推到 `main_pool_len + 相对偏移`，`try_lookup_string`
在运行时分段查找。

---

## GC 子系统 —— MagrGC（最近更新 2026-04-29 expand-magrgc-mmtk-interface）

### 接口形状（嵌入式宿主友好版）

z42 VM 的 GC 抽象由 `crate::gc::MagrGC` trait 定义，全面对齐
[MMTk](https://www.mmtk.io/) `VMBinding` porting contract（OpenJDK / V8 / Julia /
Ruby / RustPython 的事实标准 GC 抽象）。trait 在单文件内按"能力组"组织
~30 个方法，未来如需切割成 sub-trait（参考 MMTk 的 `ObjectModel` /
`Scanning` / `Collection` / `ReferenceGlue` 拆分）切割面清晰。

| 能力组 | 主要方法 | 用途 |
|-------|---------|------|
| 1. Allocation | `alloc_object` / `alloc_array` | 脚本驱动堆分配 |
| 2. Roots | `pin_root` / `unpin_root` / `enter_frame` / `leave_frame` / `for_each_root` | host pin + frame scope + GC scan |
| 3. Write barriers | `write_barrier_field` / `write_barrier_array_elem` | Phase 2+ 用，默认 no-op |
| 4. Object Model | `object_size_bytes` / `scan_object_refs` | trace / snapshot 基础设施 |
| 5. Collection | `collect` / `collect_cycles` / `force_collect` / `pause` / `resume` | GC 控制 |
| 6. Heap config | `set_max_heap_bytes` / `used_bytes` | 堆上限 / 用量 |
| 7. Finalization | `register_finalizer` / `cancel_finalizer` | 析构回调（Phase 1 仅注册不触发）|
| 8. Weak refs | `make_weak` / `upgrade_weak` | 弱引用 |
| 9. Observers | `add_observer` / `remove_observer` | GcEvent 订阅（Before/After Collect / NearHeapLimit / OOM）|
| 10. Profiler | `set_alloc_sampler` / `take_snapshot` / `iterate_live_objects` | 分配采样 + 堆快照 + 存活遍历 |
| 11. Stats | `stats` | HeapStats 快照（7 字段）|

`VmContext` 持有 `Box<dyn MagrGC>`（与 `static_fields` / `lazy_loader` 同 ownership
模型）；所有脚本驱动分配走 `ctx.heap().alloc_*(...)`；JIT helper 通过
`vm_ctx_ref(ctx).heap().alloc_*(...)` 调用同一接口；嵌入 host 通过同一
`heap()` 入口访问 root / observer / profiler 等所有能力。

命名 **MagrGC** 取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造
定制行星的传奇世界，与"管理对象生命周期"主题契合。

### Phase 1：RcMagrGC（已落地）

`crate::gc::RcMagrGC` 是 Phase 1 默认后端，**底层引用计数行为等价迁移前的直接
`Rc::new(RefCell::new(...))` 构造**，但 host-side 嵌入接口完整：

- `Value::Object` / `Value::Array` 形状不变
- `Rc::ptr_eq` 引用相等语义保留
- `RefCell` 运行时借用检查保留
- 内部状态由 `RefCell<RcHeapInner>` 持有：roots HashMap、frame_pins 栈、
  observer 列表、finalizer 表、alloc_sampler、pause counter、ID 生成器等
- `alloc_*` 通用通路 `record_alloc`：bump stats → 压力检查 → sampler 触发
- 事件分发：先 snapshot observer 列表再调用，避免回调重入引发 borrow 冲突

**已知限制（Phase 3a/3b/3c/3d/3d.1/3f/3e/3f-2/3-OOM 后）**：（无）

GC 子系统主功能至此完整。所有原始限制已解决，可投产。

> **2026-04-29 add-heap-registry（Phase 3b 完成）**：snapshot/iterate `Full` 覆盖。
>
> **2026-04-29 add-cycle-breaking-collector（Phase 3c 完成）**：环引用泄漏
> + `used_bytes` 单调递增两项限制解决 —— Bacon-Rajan trial-deletion 算法：
> mark from pinned roots → `tentative[v] = strong_count - 1` 扣减集合内部引用 →
> `tentative == 0` 清空内部 slots → alive_vec drop 时 Rc 链完成释放。
>
> **2026-04-29 add-finalizer-and-auto-collect（Phase 3d 完成）**：
> - **Finalizer 真触发**：`run_cycle_collection` 断环前从 finalizers map remove +
>   one-shot 调用回调（在 break_cycle_value 之后、alive_vec drop 之前 dispatch）
> - **内存压力自动 collect**：`alloc_object` / `alloc_array` 后调
>   `maybe_auto_collect` —— `used >= 90% max_bytes` 且距上次 auto-collect
>   增长 >= 10% limit 时自动触发 `collect_cycles`
> - **`near_limit_warned` 自动 reset**：collect 后若 `used` 已降到阈值以下
>   reset，让下次跨阈值能再发 `NearHeapLimit` 事件

> **2026-04-29 extend-native-fn-signature（Phase 1.5 完成）**：原限制"corelib 直构未迁移"
> 已解决 —— `NativeFn` 签名扩展为 `fn(&VmContext, &[Value]) -> Result<Value>`，全部 ~55
> 个 builtin 走 ctx 传参；`__obj_get_type` / `__env_args` 走 `ctx.heap().alloc_*(...)`。
> 全代码库无任何 `Rc::new(RefCell::new(...))` 直构，仅 `gc/rc_heap.rs` 内部权威实现保留
> （即所有分配都通过 GC 接口的唯一物理收口点）。

> **2026-04-29 remove-dead-value-map**：原 Phase 1 限制 #3（`alloc_map()` 占位）
> 与 `Value::Map` variant 一并删除 —— 自从 2026-04-26 extern-audit-wave0 把
> `Std.Collections.Dictionary` 改为纯脚本类后，`Value::Map` 已无创建路径。`value_to_str`
> 同步改为 exhaustive match，编译期强制覆盖所有 Value variant。

### Phase 路线（持续迭代）

| Phase | 内容 | 状态 |
|-------|------|------|
| **Phase 1** | trait MagrGC 接口 + RcMagrGC 实现 + 6 个脚本驱动 callsite 收口 | ✅ 2026-04-29 add-magrgc-heap-interface |
| **Phase 1 (扩展)** | trait 全面对齐 MMTk porting contract（10 能力组 ~30 方法）+ host-side 嵌入接口完整实现 | ✅ 2026-04-29 expand-magrgc-mmtk-interface |
| **Phase 1.5** | corelib `NativeFn` 签名扩展带 `&VmContext` + corelib 内剩余 Rc::new 迁移 | ✅ 2026-04-29 extend-native-fn-signature |
| **Phase 2** | （**跳过**）—— 直接进 Phase 3 mark-sweep，避免双重智能指针 churn | ⏭ 跳过 |
| **Phase 3a** | `GcRef<T>` / `WeakGcRef<T>` 不透明句柄抽象（backing 仍 `Rc<RefCell<T>>`，行为零变化）| ✅ 2026-04-29 introduce-gcref-handle |
| **Phase 2** | 环检测真实实现（dumpster 2.0 集成 / 自研 Bacon-Rajan 二选一） | 📋 待立项 |
| **Phase 3b** | Heap registry（`Vec<WeakRef>` 让 GC 枚举所有存活对象）+ snapshot/iterate Full coverage | ✅ 2026-04-29 add-heap-registry |
| **Phase 3c** | Trial-deletion 环回收器（保留 RC backing，断环让 Rc 链 Drop） | ✅ 2026-04-29 add-cycle-breaking-collector |
| **Phase 3d** | Finalizer 真触发（cycle collect 时调度）+ 内存压力自动 collect + near_limit_warned 自动 reset | ✅ 2026-04-29 add-finalizer-and-auto-collect |
| **Phase 3d.1** | External root scanner（VmContext static_fields / pending_exception 暴露给 cycle collector，修复漏扫 bug）| ✅ 2026-04-29 add-external-root-scanning |
| **Phase 3d.2** | 暴露 `Std.GC.Collect()` / `UsedBytes()` / `ForceCollect()` 给 z42 脚本 + 端到端 golden test 验证环回收 | ✅ 2026-04-29 expose-gc-to-scripts |
| **Phase 3f** | interp 栈扫描（`exec_function` FrameGuard RAII 把 frame.regs 暴露给 scanner，修复"outer 在 reg + outer.slot → inner" 间接可达对象被误清的 bug） | ✅ 2026-04-29 add-interp-stack-scanning |
| **Phase 3e** | `GcRef<T>` backing 升级 `Rc<GcAllocation<T>>`，wrapper Drop 自动触发已注册 finalizer（含纯 Rc Drop 路径，不仅限 cycle collect） | ✅ 2026-04-29 add-drop-time-finalizer |
| **Phase 3f-2** | JIT 栈扫描（6 个 JitFrame::new 站点 push/pop frame.regs 到 exec_stack，修复 JIT 路径 transitive bug）| ✅ 2026-04-29 add-jit-stack-scanning |
| **unify-frame-chain** | `exec_stack` / `env_arena_stack` / `call_stack` 三栈合并为单一 `Vec<VmFrame>`；任一 invoke 站点 push 全套 (regs/env_arena/name/file)，GC scanner 改单循环；附带修复 `jit_obj_new` ctor + `jit_to_str` ToString 缺 call_frame push 的 stack-trace 漏帧 bug | ✅ 2026-05-10 unify-frame-chain |
| **Phase 3-OOM** | strict OOM 模式（trait `set_strict_oom`；启用后 alloc 越过 `max_heap_bytes` 返回 `Value::Null` 不入 registry / 不 bump stats）| ✅ 2026-04-29 add-strict-oom-rejection |

至此 GC 主功能完整，可投产。后续可选迭代见下文 ["GC 后续迭代规划"](#gc-后续迭代规划) 段。

### 字符串脚本化的未来动机

当 Phase 2/3 GC 成熟（环检测 + 追踪），字符串可以从 `Value::Str(String)`
primitive 迁移成 `Value::Object(...)` 包装的脚本类（z42 源码实现 BCL `String`），
届时 z42 源码可承担更多 string 方法实现，进一步减少 Rust 端硬编码 builtin。
这与 2026-04-24 起的 simplify-string-stdlib / wave1-string-script 系列重构方向一致。

### 设计权衡：为什么不一次到位

- **Phase 1 范围严格限定为"接口收口、行为零变化"** —— commit 范围干净（纯重构），
  失败回滚成本低；环检测算法选型应在专门的 Phase 2 spec 中讨论（dumpster crate 依赖、
  STW vs 并发等独立决策点）
- **trait 形状一次设计完，让后续 phase 切换实现无需改 callsite** —— 6 处分配点
  已统一走 `ctx.heap()`，未来即使从 RcMagrGC 切到 MarkSweepHeap，调用方代码不变
- **`Value` enum 不动** —— Phase 3 引入 `GcRef<T>` 时再统一修改 PartialEq /
  JIT helper / 测试构造；Phase 1 不要把这些副带成本算进来

### GC 后续迭代规划

> 至 Phase 3-OOM 完成（2026-04-29）GC 主功能完整。下表是**可选优化轨道**，
> 按需启动独立 spec。每条提供 **What / Why / Deps / Size / Risk** 四元组，
> 让接手者不用读源码就能判断 ROI。

#### A. 性能优化轨道

##### A1. 自定义堆 allocator（移除 std Rc 开销）
- **What**：替换 `Rc<GcAllocation<T>>` backing 为自定义 region allocator + bump pointer
- **Why**：std Rc 每次 clone/drop 走原子 refcount op（单线程下 non-atomic 但仍有 cache miss）；自定义 allocator 可批量回收 + 更紧凑的对象布局
- **Deps**：无（`GcRef<T>` 句柄抽象 Phase 3a 已就位，调用方无需修改）
- **Size**：500-800 LOC，4-6 天
- **Risk**：内部 Drop 语义改变（Rc 即时释放 → allocator 批量回收）需要严格测试覆盖

##### A2. 纯 Mark-Sweep（移除 RC，统一 tracing）
- **What**：从 trial-deletion (RC + 环检测) 升到纯 tracing GC（无 Rc 元数据）
- **Why**：环检测 RC 混合算法的 trial-deletion 在 cycle 多的 workload 下 O(N²) 计算 tentative；纯 tracing 是 O(reachable)
- **Deps**：A1（自定义 allocator）+ Trace trait 全 Value variant 实现
- **Size**：1500+ LOC，10-14 天
- **Risk**：行为变化（Rc 即时释放消失 → 析构延迟到 collect）；性能 trade-off 取决于 workload，需 benchmark 决策

##### A3. Generational GC（young/old 分代）
- **What**：young 代频繁回收（短命对象集中）+ old 代低频回收 + write barriers 跟踪跨代引用
- **Why**：典型程序"分配的对象大多很快死"（generational hypothesis），young-only collect 大幅降低 pause time
- **Deps**：A1 + A2 + write barriers 真实实现 + IR codegen 在引用赋值处插桩
- **Size**：2000+ LOC，15-20 天
- **Risk**：write barrier 在每个引用赋值处插入 → 性能开销需 careful；compiler/codegen 配合工作量大

##### A4. Concurrent / incremental collector
- **What**：collect 工作分摊到多个 alloc 时间片（incremental）或后台线程（concurrent）
- **Why**：当前 STW collect 在大堆下停顿明显；分摊后单次 pause 时间有界
- **Deps**：A2（mark-sweep）+ 多线程模型（roadmap A6 backlog）
- **Size**：3000+ LOC，20-30 天
- **Risk**：数据竞争 + write barrier 复杂度高；需要 GC safepoint 协议

#### B. 嵌入式 / 可观察性

##### B1. OOM 异常抛出（替代 strict 模式返 Null）
- **What**：alloc 失败抛 `Std.OutOfMemoryException`（脚本 `try/catch` 可捕获），替代 strict 模式返 `Value::Null`
- **Why**：脚本端 OOM 处理更自然；当前返 Null 后续访问产生 NRE，丢失"why null"信息
- **Deps**：预分配 exception 实例（破启动期 alloc 循环依赖）+ ctx.set_exception 与 alloc callsite 集成
- **Size**：~150 LOC，1-2 天
- **Risk**：低；exception 实例的"启动期 alloc"循环依赖是设计点

##### B2. 软引用（SoftGcRef）
- **What**：内存压力下 GC 可主动回收的引用，介于 strong 与 weak 之间
- **Why**：缓存场景（"内存够则保留，紧张则丢弃"）现在只能手动 weak + 重建
- **Deps**：A2 mark phase 决策接口（区分软引用与强引用）
- **Size**：~200 LOC，2 天

##### B3. Heap snapshot 导出（V8 / Chrome DevTools 格式）
- **What**：序列化 `HeapSnapshot` 为 `.heapsnapshot` 兼容 Chrome DevTools / [perfetto](https://perfetto.dev/) 等工具
- **Why**：嵌入用户用熟悉的工具分析堆，无需重写 z42 专用 GUI
- **Deps**：无（纯序列化层）
- **Size**：~300 LOC，2-3 天

##### B4. 分配站点追踪（per-callsite alloc count + total bytes）
- **What**：默认 alloc_sampler 实现按 IR 站点 ID 聚合分配数据
- **Why**：定位"哪行代码在 hot allocate"，性能调优刚需
- **Deps**：编译期注入 site ID（IR 加 `alloc_site_id` 字段 + Codegen 配合）
- **Size**：~400 LOC，3-4 天
- **Risk**：site ID 在 IR 持久化、跨 zpkg 唯一性

##### B5. GC pause 直方图
- **What**：内置 metrics 直方图（Prometheus 风格 buckets：< 1ms / 1-10 / 10-100 / ...）
- **Why**：观察长尾 pause 分布，比单次 pause_us 更有信息量
- **Deps**：无
- **Size**：~200 LOC，2 天

#### C. 测试 / 调试质量

##### C1. Debug-only invariant 检查
- **What**：debug build 下每次 collect 后 assert（无 dangling Rc / registry 一致性 / finalizers_pending == has_finalizer 总数 / 等）
- **Why**：早暴露内部一致性 bug，防止生产环境疑难杂症
- **Deps**：无
- **Size**：~150 LOC，1-2 天

##### C2. Stress test（property-based）
- **What**：proptest / quickcheck 生成随机 alloc / drop / pin / collect 序列，长跑下 verify 不 crash + 内存不 leak
- **Why**：手工 unit test 难覆盖 alloc 与 collect 的交错状态空间
- **Deps**：proptest crate
- **Size**：~300 LOC，2-3 天

##### C3. Multi-VmContext 隔离压测
- **What**：多个 VmContext 实例并行运行（线程间隔离），verify GC 状态不互相污染
- **Why**：嵌入用户可能创建多 VM；验证 Phase 3 多实例隔离设计真的成立
- **Deps**：无
- **Size**：~200 LOC，2 天

#### D. 终极方向：MMTk 集成

##### D1. MMTk 后端实现
- **What**：实现 [MMTk](https://www.mmtk.io/) `VMBinding` trait，把 RcMagrGC 替换为 MMTk-backed GC（多 collector 可选：SemiSpace / GenImmix / MarkSweep / Immix / GenCopy）
- **Why**：MMTk 是工业级研究项目，被 OpenJDK / V8 / Julia / Ruby / RustPython 采用；享受 ~30 年 GC 算法成果
- **Deps**：A1 + A2（自定义 allocator + tracing）；trait 形状已对齐 MMTk porting contract
- **Size**：4-8 周（包括稳定 + benchmark）
- **Risk**：高 —— 引入大型 crate 依赖；ABI 边界 careful；但 trait 抽象层可以 ABI-shield

#### 优先级建议

如以工程成熟度优先：

1. **C1 + C2 + C3**（debug invariants + stress + multi-VM）—— 巩固现有质量
2. **B1**（OOM exception）—— 嵌入用户最常请求的 ergonomics
3. **B5**（pause 直方图）—— 低成本可观察性
4. **B3**（heap snapshot 导出）—— 让现有工具链可用

如以性能为优先：

1. **A1**（自定义 allocator）—— 单刀直入的常数因子优化
2. **A2**（mark-sweep）—— 算法路径简化的前提
3. **B4**（alloc site tracking）—— 找出热点
4. **A3 / A4**（generational / concurrent）—— 长期投资

如以"赶上工业级"为优先：

直接跳到 **D1**（MMTk 集成），跳过 A1-A4 自研路径。trait 形状已对齐
MMTk porting contract，集成成本主要在 ABI shim + 调优。

---

## 延伸阅读

- `docs/design/runtime/ir.md` — IR 指令集、zbc 二进制格式
- `docs/design/runtime/jit.md` — Cranelift JIT 后端设计
- `docs/design/runtime/execution-model.md` — ExecMode 注解、interp/JIT/AOT 切换语义
- `docs/design/stdlib/overview.md` — stdlib 三层架构（intrinsics / HAL / script BCL）
- `.claude/rules/runtime-rust.md` — Rust VM 开发规范（错误处理、测试组织、Value 类型约定）
