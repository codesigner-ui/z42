# z42 VM 内部实现原理

> **目的**：记录 Rust VM 的内部数据结构、算法、加载策略与关键设计决策。
> 让新接手者不必阅读大量源码即可理解"为什么这样设计"。
> 面向 VM 开发者，不面向 z42 语言使用者。
>
> 使用者视角请看 `docs/design/ir.md`、`docs/design/execution-model.md`。

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

- `static_fields: RefCell<HashMap<String, Value>>` — 用户类 static 字段
- `pending_exception: RefCell<Option<Value>>` — JIT extern "C" 边界异常槽位
- `lazy_loader: RefCell<Option<LazyLoader>>` — 按需 zpkg 加载器

API 方法都用 `&self`（内部 RefCell），调用方代码风格基本不变。多 VmContext
实例完全隔离 —— 这是 review2 §3 的设计目标兑现。详见
[`object-protocol.md`](object-protocol.md) 与 review2 §3 / §5.5 / §5.2。

---

## VM 启动流程

```
z42vm <file>
  │
  ├── resolve_libs_dir()                       [main.rs]
  │      → $Z42_LIBS | <binary>/../libs | <cwd>/artifacts/z42/libs
  │
  ├── 5.1b 加载 z42.core.zpkg (eager，隐式 prelude)
  │      → modules[0]
  │      → initially_loaded_zpkgs = ["z42.core.zpkg"]
  │
  ├── 5.1c 加载 user artifact (.zbc / .zpkg)
  │      → user_artifact.module + dependencies + import_namespaces
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
2. allocate ScriptObject(type_desc, slots=fields.len() × Null)
3. ctor_fn = module.func_index[ctor_name] | lazy_loader.try_lookup_function(ctor_name)
4. if ctor_fn: exec_function(ctor_fn, [obj, ...args])
   else:       skip ctor call（默认无参 ctor 语义；TypeChecker 已确保
               有显式 ctor 时 ctor_name 命中）
5. frame[dst] = obj
```

**与 VCall 对齐的核心**：编译期完整 overload resolution，VM 直查 `ctor_name`，
不再做 `${class}.${simple}` 名字推断（2026-04-26 add-objnew-ctor-name 重构）。
`ctor_name` 含 `$N` arity suffix（重载场景）；单 ctor 时无 suffix。

### TypeDesc 结构

```rust
struct TypeDesc {
    name: String,                          // FQ 类名，e.g. "Std.Collections.Stack"
    base_name: Option<String>,             // 父类 FQ 名
    fields: Vec<FieldSlot>,                // 布局顺序，基类字段在前
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

**已知限制（Phase 3a/3b/3c/3d/3d.1 后）**：

1. **Finalizer 仅在 collect_cycles 时触发**：纯 Rc Drop 路径不触发 → Phase 3e
2. **`OutOfMemory` 仅通知不拒绝** → Phase 3e+
3. **interp / JIT 栈帧 regs 暂未对接为 GC roots** → Phase 3f

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
| **Phase 3e**（可选）| 替换 GcRef backing 为自定义堆 + 真 mark-sweep（性能 / generational 准备）| 📋 待立项 |
| **Phase 3f** | Cranelift stack maps（interp + JIT 路径下 GC 安全点） | 📋 待立项 |
| **Phase 4+** | 分代 / 并发 / MMTk 集成 | 长期 |

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

---

## 延伸阅读

- `docs/design/ir.md` — IR 指令集、zbc 二进制格式
- `docs/design/jit.md` — Cranelift JIT 后端设计
- `docs/design/execution-model.md` — ExecMode 注解、interp/JIT/AOT 切换语义
- `docs/design/stdlib.md` — stdlib 三层架构（intrinsics / HAL / script BCL）
- `.claude/rules/runtime-rust.md` — Rust VM 开发规范（错误处理、测试组织、Value 类型约定）
