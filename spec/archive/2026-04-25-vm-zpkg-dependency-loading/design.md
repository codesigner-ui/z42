# Design: VM zpkg-based Dependency Loading

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  VM 启动 (main.rs)                                               │
│  1. 加载 z42.core.zpkg（eager，隐式 prelude）                     │
│  2. 加载 user main artifact（.zbc / .zpkg）                      │
│  3. 构建 "declared_zpkgs" 集合：                                 │
│       - .zpkg 主模块：直接用 its ZpkgDep 列表                     │
│       - .zbc  主模块：从 import_namespaces 反查 zpkg（扫 libs）   │
│     z42.core 标为 loaded；其他标为 declared-but-not-loaded       │
│  4. install(LazyLoader { declared, loaded })                    │
└─────────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────────┐
│  Interp 运行时：Call / ObjNew miss                               │
│  1. 查 already-loaded function_table / type_registry            │
│  2. miss → 遍历 declared - loaded 集合：                         │
│       for zpkg_file in declared_but_not_loaded (按 declared 顺序)│
│         load_artifact(zpkg_file)                                │
│         merge functions / type_registry / string_pool           │
│         **递归展开 its ZpkgDep 进 declared 集合**                │
│         mark zpkg_file as loaded                                │
│         if target function found → return                       │
│  3. 所有候选耗尽仍未命中 → 真正 undefined                         │
└─────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 懒加载触发策略 —— "batch load + retry"

**问题：** Call miss 时如何决定加载哪个候选 zpkg？

**选项：**
- A. **Batch load**：一次加载所有 declared-but-not-loaded 的 zpkg，然后查函数
- B. **Incremental load**：逐个加载、每加载一个就重查，命中即停
- C. **Targeted load**：分析 func_name 前缀，从 "declared" 集合中的 zpkg
  metadata（namespaces 字段）筛选候选，只加载包含该 namespace 的 zpkg

**决定：选 C**

**理由：**
- 利用 zpkg 的 `namespaces` 元数据（已有）做 **精确路由**，避免 B 的顺序依赖
- 比 A 节省：单次 miss 通常只需加载 1 个 zpkg，不必加载所有声明依赖
- 回退路径：若 C 匹配不到任何候选，降级为 B 遍历（保留兼容性）
- C 等价于 C# CLR 基于 AssemblyRef + TypeRef 的精确查找，语义清晰

### Decision 2: 保留 `resolve_namespace` 但改语义

**问题：** 现在 `resolve_namespace` 单返回 `Option<PathBuf>` 并在歧义时 bail。

**决定：** 改签名为 `resolve_namespace(ns, ...) -> Result<Vec<PathBuf>>`。
返回所有声明该 namespace 的 zpkg 路径列表，不 bail。

**理由：**
- 编译期 / 诊断工具仍可能想知道 "哪些 zpkg 提供某 namespace"
- 消除 ambiguous 语义更干净（歧义现在是合法的）
- 懒加载路径不再调用此函数，改走新增 `resolve_dependency(zpkg_name)`

### Decision 3: `.zbc` 主模块的依赖推断 —— 仅在启动时做一次

**问题：** `.zbc` 没有 DEPS section。如何获知其依赖？

**决定：** 启动时从 `import_namespaces` 反查 libs/*.zpkg 的 namespaces，
把匹配到的 zpkg 加入 declared_zpkgs 集合。仅在 VM 启动时做一次。

**理由：**
- `.zbc` 的 `import_namespaces` 已经是编译期从 Call 指令反推出来的（见
  [loader.rs:208](src/runtime/src/metadata/loader.rs#L208)），语义等价于
  "我用到了这些 namespace 的符号"
- 一次性扫描无性能负担（libs 目录只有个位数 zpkg）
- 运行时不再扫 libs（Call miss 时仅在 declared 集合中查找），保持懒加载快速路径

### Decision 4: 循环依赖 / 重复加载保护

**问题：** A zpkg 依赖 B，B 依赖 A（或自依赖），如何避免无限递归？

**决定：** `loaded_zpkgs` 集合在 **加载开始前** 就插入 zpkg_file 名，
再展开其 dependencies。若某个依赖已在集合中（任何状态），跳过。

**理由：** 标准的"着色"算法（白 → 灰 → 黑），防止 re-entry。

### Decision 5: ConstStr 索引偏移策略（保持现有）

**问题：** 懒加载合并 function 时，ConstStr idx 需要重映射到聚合 string_pool。

**决定：** 保持现有 `remap_const_str` + `main_pool_len + cumulative_lazy_pool_len`
偏移机制不变（见 [lazy_loader.rs:154](src/runtime/src/metadata/lazy_loader.rs#L154)）。

**理由：** 与触发键无关；正交机制。不动。

### Decision 6: Type / Function 冲突处理 —— 后加载者保留 vs 前加载者保留

**问题：** 两个 zpkg 都定义了同名类型或函数（不应该发生但需兜底），如何处理？

**决定：** **前加载者保留**（first-wins）。使用 `HashMap::entry().or_insert(...)` 语义。

**理由：**
- 预期这种情况不应发生（编译期类型检查应捕获重名）
- 若发生，稳定的行为比"最后加载者覆盖"更安全
- 与 C# CLR 一致（同名 TypeDef 在不同 assembly 会加载失败或优先已加载）
- 作为诊断，在 tracing::warn 记录冲突

## Implementation Notes

### 新增 / 修改数据结构（lazy_loader.rs）

```rust
struct LazyLoader {
    libs_dir: Option<PathBuf>,
    main_pool_len: usize,
    string_pool: Vec<String>,

    // MODIFIED: 从 namespace-based 改为 zpkg-based
    loaded_zpkgs: HashSet<String>,          // already-loaded zpkg file names
    declared_zpkgs: HashMap<String, ZpkgCandidate>, // declared-but-not-loaded

    // 函数 / 类型表合并（保持）
    function_table: HashMap<String, Arc<Function>>,
    type_registry:  HashMap<String, Arc<TypeDesc>>,
}

struct ZpkgCandidate {
    file_path: PathBuf,       // 绝对路径
    namespaces: Vec<String>,  // 该 zpkg 声明的 namespaces（从 metadata 读）
    dependencies: Vec<String>,// 该 zpkg 的依赖 zpkg 文件名（传递展开用）
}
```

### 新增 API

```rust
/// 初始化 LazyLoader，传入 main module 的依赖列表
pub fn install_with_deps(
    libs_dir: Option<PathBuf>,
    main_pool_len: usize,
    declared_zpkgs: Vec<ZpkgCandidate>,
    initially_loaded: Vec<String>, // e.g. ["z42.core.zpkg"]
);

/// 按 func_name 查找，miss 时触发懒加载（Decision 1 的 C 策略）
pub fn try_lookup_function(func_name: &str) -> Option<Arc<Function>>;

/// 按 class_name 查找，miss 时触发懒加载
pub fn try_lookup_type(class_name: &str) -> Option<Arc<TypeDesc>>;
```

旧 `install(libs_dir, main_pool_len)` 保持签名兼容，内部委托给
`install_with_deps` 并传入空 declared 集合（退化为"无声明依赖，全部跳过"模式）。
但 main.rs 启动路径必须改为调用 `install_with_deps`，否则懒加载永远不会触发。

### main.rs 改动摘要

```rust
// 现状（简化）：
install(libs_dir, final_module.string_pool.len());

// 改为：
let (declared, initially_loaded) = build_declared_zpkgs(
    &user_artifact,
    &libs_dir,
    /* z42.core already loaded */
);
install_with_deps(libs_dir, pool_len, declared, initially_loaded);
```

其中 `build_declared_zpkgs`：
- 若 `user_artifact` 来自 `.zpkg`：用其 `dependencies` 字段
- 若来自 `.zbc`：扫 libs_dir 的所有 zpkg，匹配 `import_namespaces` 得候选列表

### 关键不变量

1. `loaded_zpkgs` 是 `declared_zpkgs` keys 的子集之后的扩展；任何 zpkg 在
   加载前必须先进入 declared 集合（防止加载未声明的东西）。
2. 每次 `try_lookup_function` 至多触发 1 次 zpkg 加载（Decision 1 策略 C），
   除非策略 C 匹配不到、降级到遍历全 declared 集合。
3. `z42.core` 永远在 `loaded_zpkgs` 的初始集合中（由 main.rs 预加载路径维护），
   不经过懒加载。

### Decision 7: 编译期 TsigCache 支持 namespace 跨 zpkg（Scope 扩展）

**问题：** VM 侧改造完成后 W1 仍有测试失败。根因：编译期 `TsigCache._nsToPath`
是 `Dictionary<string, string>`，`RegisterNamespace` 用 `TryAdd` first-wins；
W1 后 `Std.Collections` 多 zpkg 共享时，后扫到的 zpkg 被丢弃，Stack / Queue
的 TSIG 元数据缺失 → `QualifyClassName` 退化为 bare 名 → IR 指令目标错误。

**决定：** 扩展 scope，把编译期 `TsigCache` 一并改造：

```csharp
// Before
private readonly Dictionary<string, string> _nsToPath = ...;
public void RegisterNamespace(string ns, string p) { _nsToPath.TryAdd(ns, p); }

// After
private readonly Dictionary<string, List<string>> _nsToPaths = ...;
public void RegisterNamespace(string ns, string p) {
    if (!_nsToPaths.TryGetValue(ns, out var list)) {
        list = new List<string>();
        _nsToPaths[ns] = list;
    }
    if (!list.Contains(p)) list.Add(p);
}
```

`LoadForUsings` / `LoadAll` 聚合所有路径并加载。`_cache` 仍按 zpkgPath key
（避免重复加载同一 zpkg）。

**理由：**
- VM 和编译器**对称支持**才能让 W1 端到端闭环
- `ImportedSymbolLoader.Load` 已经能处理"多个 ExportedModule 同 namespace"
  （for 循环逐个登记 + `!ContainsKey` first-wins），只是 `TsigCache` 这一层把
  第二个 zpkg 的整个 TSIG 丢了，导致第二个 zpkg 的类根本没进循环
- 改动面小（一个 Dictionary 类型 + 两个方法实现），同 C# assembly 模型对齐

## Testing Strategy

### 单元测试（`lazy_loader_tests.rs`）

- `test_load_single_dependency`：main 声明 1 个依赖，Call miss 触发加载
- `test_declared_not_loaded_transitive`：A 依赖 B，B 依赖 C，加载 A 时
  B、C 自动进入 declared 集合
- `test_same_namespace_multi_zpkg`：两个 zpkg 声明同 namespace，各定义
  不同函数，连续调用都命中（**对 W1 场景的直接回归**）
- `test_undeclared_zpkg_not_loaded`：即使 libs 目录有某 zpkg，若未声明依赖，
  Call miss 也不会加载，最终报 undefined
- `test_cyclic_dependency_no_hang`：A 依赖 B，B 依赖 A；加载终止，不死循环
- `test_zpkg_load_only_once`：重复 Call miss 同一 zpkg 的不同函数，只加载 1 次
- `test_incremental_pool_offset`：懒加载多个 zpkg 后，各自 ConstStr 索引正确
  映射到聚合池（保持现有覆盖）

### 回归测试

- VM golden tests `test-vm.sh` 全绿（含 W1 挂起的 `run/77_stdlib_stack_queue`）
- JIT / interp 双模式通过
- 编译器单元测试 `dotnet test` 全绿

### 端到端验证

- W1 文件已就位的 working tree 跑 `./scripts/build-stdlib.sh` → `test-vm.sh`
  全绿 = W1 解锁成功

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| `resolve_namespace` 签名变更打破其他调用点 | 低（仅 main.rs JIT 路径 + lazy_loader.rs 两处调用） | 同一 PR 内全部修正；JIT 路径改用 `ZpkgDep.file` |
| `.zbc` 依赖推断失败（import_namespaces 无匹配 zpkg） | 低 | warn + 跳过，保持与当前"miss 时报错"行为一致 |
| 已有 L3-G4d 懒加载 ctor 测试破坏 | 低 | Decision 1 策略 C 基于 namespace metadata 精确路由，等价于原 namespace 查找行为 |
| 冲突 first-wins 导致静默覆盖 bug | 低 | tracing::warn 记录；单测覆盖冲突场景 |
