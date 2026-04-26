# src/libraries — z42 标准库源码

## 职责

z42 标准库的 `.z42` 源文件。每个库是独立的 z42 包，通过 `build-stdlib.sh` 编译为 `.zpkg` 产物后供用户程序引用。

## 库列表

| 目录 | 包名 | 内容 |
|------|------|------|
| `z42.core/` | `z42.core` | 核心类型：`Object`、`String`、`Type`、`Assert`、`Convert`、核心接口；`Collections/` 子目录内含基础泛型集合 `List<T>` / `Dictionary<K,V>` |
| `z42.collections/` | `z42.collections` | 次级集合类型：`Queue`、`Stack`（未来 `LinkedList` / `SortedDictionary` / `PriorityQueue`） |
| `z42.io/` | `z42.io` | IO 类型：`Console`、`File`、`Path`、`Environment` |
| `z42.math/` | `z42.math` | 数学函数：`Math` |
| `z42.text/` | `z42.text` | 文本处理：`StringBuilder`、`Regex` |

## 实现规范（必须遵守）

### 1. Script-First：优先脚本实现

**尽可能把逻辑放到 `.z42` 脚本实现，减少 VM 侧的 extern / intrinsic。**

- 新增方法默认用 `.z42` 脚本实现，即使暂时性能不优
- 性能问题延后优化（profile → JIT 优化 → 必要时再下沉为 intrinsic）
- 现存 extern 逐步评估下沉：若能用"更小的 intrinsic 核 + 脚本组合"表达，
  优先迁移。例：`Contains` / `IndexOf` / `Trim` / `Substring` 已迁脚本；
  `String.Length` 等仍是 extern 的方法，在更基础原语（如 `char[]` 视图）
  就绪后也应评估是否能下沉，不把"性能担忧"当作保留 extern 的默认理由。
- 只有真正无法用脚本表达的原语才保留 extern：内存布局 / 原子指令 / 底层
  分配 / 与 VM ABI 绑定的协议方法（`Equals` / `GetHashCode` / `ToString`）。

### 2. VM 接口集中在 z42.core

**VM 提供的 extern / intrinsic 接口只能出现在 `z42.core`。**

其他包（`z42.collections` / `z42.math` / `z42.text` / ...）**一律不允许**
声明任何 VM extern，必须通过调用 `z42.core` 的公开 API 间接使用 VM 能力。

**唯一例外：`z42.io`。**
`z42.io` 依赖**另一个** native 库（文件系统 / 控制台 / 环境变量等操作系统
能力），该 native 接口与 "VM corelib intrinsic" 是两套独立通道——它不占
用 VM intrinsic 预算，走的是 host function (FFI) 机制。除 `z42.io` 外，
其他包禁止引入任何 native 依赖。

> 目的：保持 VM 表面最小、可审计；stdlib 绝大部分逻辑由脚本驱动，便于
> 自举、调试和演进。新增 VM extern 视同新增语言原语，需走 vm 类型完整
> 变更流程。

---

## 构建

```bash
./scripts/build-stdlib.sh           # debug
./scripts/build-stdlib.sh release   # release
```

产物输出到 `artifacts/z42/libs/*.zpkg`（已在 `.gitignore` 中，不纳入版本控制）。

## 修改后

修改任意 `.z42` 源文件后必须重新运行 `build-stdlib.sh` 更新 zpkg 产物。

---

## 未来计划（按需补齐，无空目录占位）

> 仅作 roadmap 备忘。**实际拉新包时再建目录** — 避免半成品占位包污染构建。

### 已规划但未启动

| 包 | 内容 | 阶段 | 触发条件 |
|----|------|------|---------|
| `z42.diagnostics` | `Debug` / `Trace` / `Stopwatch` / `Assert*` 扩展 | L2 | 性能调优 / 调试日志需求出现 |
| `z42.threading` | `Thread` / `Mutex` / `Atomic*` / `Channel` | L3 | 并发模型设计完成（`Rc<RefCell>` → `Arc<Mutex>` 配套）|
| `z42.async` | `Task<T>` / `async`/`await` runtime / `ValueTask` | L3 | 关键字 `async` / `await` parser 完成 |
| `z42.net` | `Socket` / `HttpClient` / `Url` | L3+ | 异步运行时就绪后 |
| `z42.json` | `JsonReader` / `JsonWriter` / `JsonNode` | L3+ | 反射 (L3-R) 完成（自动序列化）|
| `z42.test` | `Test` 注解 / `Assert*` / Runner | L2/L3 | 用户脚本测试需求 |
| `z42.linq` | `Where` / `Select` / `OrderBy` 扩展（基于 `IEnumerable<T>`）| L3 | Lambda + IEnumerable codegen 升级 |
| `z42.numerics` | `BigInteger` / `Complex` / 矩阵基础 | L3 | 数值计算需求 |
| `z42.crypto` | 哈希 / 对称加密 / 签名（封装 native 库）| L3+ | 安全场景需求 |
| `z42.compression` | gzip / zstd 封装 | L3+ | 文件 / 网络压缩需求 |

### 既有包的扩展计划

| 包 | 待补齐 |
|----|--------|
| `z42.core` | `Nullable<T>` 显式类型（暂用语言级 `T?`，独立类型留待系统设计）<br>`KeyValuePair<K,V>`（Dictionary 实现 `IEnumerable` 需要）<br>`Range` / `Index`（C# 8 风切片）<br>`Tuple<...>`（多返回值；当前 z42 无 tuple 类型）|
| `z42.collections` | `LinkedList<T>` / `SortedDictionary<K,V>` / `PriorityQueue<T>` / `ImmutableArray<T>`<br>List / Dictionary 实现 `IEnumerable<T>`（端到端 foreach IEnumerator 路径）|
| `z42.io` | `Stream` / `BufferedStream` / `MemoryStream`<br>`TextReader` / `TextWriter` 抽象类<br>`Directory` / `FileInfo` / `DirectoryInfo`<br>`Encoding` (UTF-8 / UTF-16)|
| `z42.math` | `Random`（PRNG，Mersenne Twister 或 PCG）<br>`Complex` / `Vector*`（如不拆 `z42.numerics`）|
| `z42.text` | `Encoding` 体系（与 `z42.io` 协调）<br>`StringReader` / `StringWriter`<br>`Regex` 完整实现（当前占位）|

### 跨包 backlog

- `IComparer<T>` / `IEqualityComparer<T>` 接入 List.Sort / Dictionary ctor 重载
- `IEnumerable<T>` 接入 foreach codegen（当前 codegen 仅识别 Count + get_Item 鸭子协议）
- `IFormattable` 接入 `string.Format` / `$"{x:format}"` 字符串插值格式说明符
- 通用 generic interface dispatch 修复（TypeChecker 不识别 `IComparer<int>` 等的 TypeArgs，阻塞接口变量直接调用）

### 不规划做的（明确否决）

- ❌ "完整 BCL 移植"：z42 仅取 C# BCL **常用 80%**，避开 LINQ-to-SQL / WPF / WCF / Remoting 等历史包袱
- ❌ Reflection-heavy 序列化（XmlSerializer 等）：等 L3-R 反射完成后再考虑，且只做 JSON
- ❌ AppDomain / 卸载：与 z42 lazy-loader 模型不契合
- ❌ 静态类反射创建（`Activator.CreateInstance`）：等 L3-R
