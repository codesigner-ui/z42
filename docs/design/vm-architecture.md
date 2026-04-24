# z42 VM 内部实现原理

> **目的**：记录 Rust VM 的内部数据结构、算法、加载策略与关键设计决策。
> 让新接手者不必阅读大量源码即可理解"为什么这样设计"。
> 面向 VM 开发者，不面向 z42 语言使用者。
>
> 使用者视角请看 `docs/design/ir.md`、`docs/design/execution-model.md`。

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

### 为什么懒加载用 thread_local

```rust
thread_local! { static STATE: RefCell<Option<LazyLoader>> = ... }
```

- **避免 Mutex**：懒加载是编译期或 run-to-completion 路径，不需要跨线程共享
- **测试友好**：`install` / `uninstall` 成对使用，测试隔离性由 thread_local
  自动保证

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

## 延伸阅读

- `docs/design/ir.md` — IR 指令集、zbc 二进制格式
- `docs/design/jit.md` — Cranelift JIT 后端设计
- `docs/design/execution-model.md` — ExecMode 注解、interp/JIT/AOT 切换语义
- `docs/design/stdlib.md` — stdlib 三层架构（intrinsics / HAL / script BCL）
- `.claude/rules/runtime-rust.md` — Rust VM 开发规范（错误处理、测试组织、Value 类型约定）
