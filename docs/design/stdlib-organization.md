# z42 标准库模块划分规则

> **目的**：给出 z42 stdlib 包（zpkg）划分的可重用规则，参考 C# BCL + Rust std 实践，
> 让后续添加新包（z42.threading、z42.json、z42.linq 等）有明确的依据。
>
> **受众**：stdlib 设计者、新包提案者、reviewer。
> **不是**：用户使用文档（用户视角看 `docs/design/language-overview.md`）。

---

## TL;DR — 核心规则

1. **每个 zpkg 是分发单元，对应 `[project] name = "z42.<domain>"`**；命名空间可跨 zpkg。
2. **z42.core = 类型 + protocol 的最小集**，对齐 C# CoreLib 的"必备类"范围；高层扩展（数值 trait / 格式化 / 比较器策略 / LINQ 等）**走非 core 包 + cross-zpkg impl**。
3. **z42.core 是隐式 prelude** — 所有程序自动加载 + 不可声明依赖。
4. **VM extern 只能在 z42.core**（lock-in 规则，见 `libraries/README.md`）；`z42.io` 是仅有的 host FFI 例外（OS 能力走单独通道）。
5. **包按层叠层分类**（L0 → L1 → L2 → L3），上层依赖下层，**禁止反向依赖**。
6. **「Extension over Expansion」**：给已有类型加方法时，**优先在外部包通过 `impl Trait for Type` 扩展**（L3-Impl2 已支持），而非把方法塞回类型所在包。这是 z42 与 C# BCL 的核心差异化 —— core 不再是"什么都往里塞"。
7. **每个新包必须明确层级 + 依赖闭包 + 是否可纯脚本化**；不满足全部的不开新包，留 backlog。

---

## 现状（2026-04-26）

| 包 | 层级 | 内容（节选） | extern？ |
|----|----|----|----|
| `z42.core` | L0 | Object、primitive 类型（int/long/double/float/char/bool/string）、Type、Convert、Assert、Exception 树、核心接口（IComparable / IEquatable / IComparer / IEqualityComparer / IFormattable / IDisposable / IEnumerable / INumber）、Collections/{List, Dictionary} | ✅ VM intrinsic |
| `z42.collections` | L1 | Stack、Queue（计划：LinkedList、SortedDictionary、PriorityQueue） | ❌ 纯脚本 |
| `z42.math` | L1 | Math 静态方法 | ❌ 纯脚本（包装 z42.core 原语） |
| `z42.text` | L1 | StringBuilder（纯脚本，2026-04-26 迁移）；Regex 占位 | ❌ 纯脚本 |
| `z42.io` | L2 | Console、File、Path、Environment | ✅ host FFI（仅此包例外） |

> 历史角度：W1（2026-04-25）把 List / Dictionary 从 z42.collections 上提到 z42.core/Collections/，对齐 C# BCL 把 `System.Collections.Generic.List<T>` 放在 CoreLib 的做法（每个程序都在用）。

---

## 参考体系：C# BCL vs Rust std

### C# BCL（CLR base class library）

**结构**：
```
System.Private.CoreLib (CoreLib)
  ├── System.{Object, String, Int32, Boolean, ...}
  ├── System.{IComparable, IDisposable, IEquatable<T>, ...}
  ├── System.Collections.Generic.{List<T>, Dictionary<K,V>, ...}
  ├── System.IO.{File, Path, Stream, Console, ...}
  ├── System.Text.{StringBuilder, Encoding, ...}
  └── System.Threading.{Thread, Monitor, ...}
System.Console.dll                  ← 现代 .NET 拆出来
System.Linq.dll                     ← LINQ 扩展
System.Net.Http.dll                 ← HTTP
System.Text.Json.dll                ← JSON
```

**关键观察**：
- **CoreLib 巨大** —— 所有"每个程序都可能用到"的东西都进 CoreLib（包括 File / Console / StringBuilder / Thread）
- **namespace ≠ assembly** —— `System.IO.File` 在 CoreLib，`System.IO.FileSystemWatcher` 在另一个 dll。namespace 是组织逻辑，assembly 是分发单元
- **后续拆分** —— 新 .NET 把 `System.Console`、`System.Linq`、`System.Net` 等拆成独立 dll，让 trim / publish 能去掉不用的部分。但 namespace 没变。
- **顶级层级**：`System` (≈ core) → `System.<domain>` (IO / Text / Collections / Threading / Net / Linq / Json)

### Rust std

**结构**：
```
core (no_std, no allocator)
  ├── core::{Option, Result, mem, ptr, slice, str, ...}
  ├── core::cmp / hash / fmt / iter
  └── core::ops / convert
alloc (with allocator, no OS)
  ├── alloc::{Vec, String, Box, Rc, BTreeMap, HashMap, ...}
  └── alloc::fmt
std (full OS)
  ├── std::io::{stdin, stdout, File, BufReader, ...}
  ├── std::fs / path / env / process / thread / sync / time
  └── std::net / collections (re-export)
```

**关键观察**：
- **三层**：core (零依赖) → alloc (需要堆分配器) → std (需要 OS)。每层是单独的 crate，但用户大多透明用 std
- **std 是单一 crate**，里面按 module 分（`std::io`、`std::collections`）；所以"包"就一个，"模块"很多
- **第三方负责扩展**：`tokio` / `serde` / `reqwest` 不在 std，由 cargo 生态处理。和 .NET 把 `System.Text.Json` 放官方 dll 风格不同
- **顶级 module 命名**：io / fs / path / env / collections / sync / thread —— 都是单层

### 对比小结

| 维度 | C# BCL | Rust std |
|---|---|---|
| 分发单位 | dll (assembly) | crate |
| 命名空间深度 | 嵌套（System.IO.File） | 单层（std::io::File） |
| Core 内容 | 大（Console/File/Thread 都在） | 极小（core 无 OS、无堆） |
| 扩展机制 | 官方 NuGet 包 + 第三方 | 第三方 crates |
| trait/interface 实现位置 | 跟着类型定义 | trait/类型可以在不同 crate（孤儿规则约束） |

---

## z42 的设计选择（差异化）

z42 处于 **C# 风（包就是分发单位 + 大 stdlib 集合）** 与 **Rust 风（小 std + 第三方繁荣）** 之间，但加了两个独有约束：

1. **包名 = zpkg 文件名** — `z42.core.zpkg`、`z42.io.zpkg` 是物理产物。命名空间 `Std.<X>` 可以跨多个 zpkg（W1 决策）
2. **VM extern 只在 z42.core**（host FFI 仅 z42.io 例外） — 决定了"哪些功能必须捆在哪个包"

z42 的层级模型把 C#/Rust 两边的精华都拿过来：

```
L0  z42.core              (隐式 prelude，VM intrinsic 唯一来源)
        ↑ 依赖
L1  z42.collections, z42.math, z42.text, ...
    （纯脚本，按 domain 分；可任意组合用）
        ↑
L2  z42.io                (host FFI 例外，OS 能力)
    z42.threading         (待 L3 并发模型；走 host FFI / VM 协作)
    z42.async             (待 L3 async；同上)
        ↑
L3  z42.net, z42.linq, z42.json, z42.test, z42.diagnostics, ...
    （依赖 L2 runtime 服务）
```

---

## 划分规则（推荐操作清单）

### R1 — 「应该进 z42.core 吗？」决策树

判断一个类型/接口是否进 z42.core：

```
该类型是否满足以下任一条件？
  (A) 是 VM intrinsic 接口（extern / [Native]）        → ✅ 必须进 z42.core
  (B) 是 primitive 类型的 stdlib 表示（struct int / class String）  → ✅ 进 z42.core
  (C) 是被 **类型系统/语言运行时** 直接消费的协议
      （Object 三件套：Equals/GetHashCode/ToString；Exception 基类；
        IComparable<T>/IEquatable<T>：被泛型约束、catch 子句、is-pattern 等触达）
      → ✅ 进 z42.core
  (D) 是基础容器类型本身（List<T>、Dictionary<K,V>） → ✅ 进 z42.core
      （只是**类型本身**；它的扩展方法 / 比较器 / 序列化适配走 L1 impl）
否则 → ❌ 不要进 z42.core，按下游决策树看 R2-R4
```

> **设计目标**：z42.core 是"类型系统的物理底座"，不是"功能仓库"。
> 凡是可以通过 `impl Trait for Type` 在外部包追加的能力，**默认放外部**。

**与 C# BCL 的差异化（重要）**：

C# CoreLib 把 `IComparer<T>` / `IFormattable` / `IConvertible` / 大量 `IEnumerable<T>` 扩展方法
都塞在了 CoreLib，是因为 C# 没有真正的"跨 assembly trait 扩展"机制（extension method 只是
语法糖，不能让 `int` 后期才"实现"`INumber<int>` 接口）。z42 有了 cross-zpkg `impl Trait for
Type`（L3-Impl2，2026-04-26），**应主动让 core 比 C# CoreLib 更瘦**：

| 协议 / 类型 | C# 放哪 | z42 应该放哪 | 理由 |
|---|---|---|---|
| `Object` 协议（Equals 等）| CoreLib | `z42.core` ✅ | 类型系统直接消费 |
| `Exception` 基类 + 常用子类 | CoreLib | `z42.core` ✅ | catch 子句 + 控制流核心 |
| `IComparable<T>` / `IEquatable<T>` | CoreLib | `z42.core` ✅ | 泛型约束 / 容器查找内核语义 |
| `List<T>` / `Dictionary<K,V>` 类型 | CoreLib | `z42.core` ✅ | "实质类型"被 syntactic-sugar / 泛型推断 hardcode |
| `IComparer<T>` / `IEqualityComparer<T>` | CoreLib | `z42.collections` 🔄 | 是"为容器策略"的接口，单 domain |
| `IFormattable` / `Std.String.Format` | CoreLib | `z42.text` 🔄 | 格式化是文本 domain |
| `INumber<T>` / 数值 trait | CoreLib | `z42.numerics` 🔄（未来包）| 数值代数 domain |
| `IEnumerable<T>` 类型本身 | CoreLib | `z42.core` ✅ | foreach 鸭子协议依赖 |
| `IEnumerable<T>` LINQ 扩展 | System.Linq.dll | `z42.linq` 🔄（未来包）| 高层扩展，纯 impl 套路 |
| `int.op_Add` 等 primitive 数值算子 | CoreLib（运算符重载语法）| z42.numerics 通过 `impl INumber<int> for int` 🔄 | primitive 类型本身在 core，trait 实现在 numerics |

🔄 = z42 与 C# 的差异决策；✅ = 与 C# 一致。

**判断规则**：要求一个类型/接口进 z42.core 的人，必须能回答 *"为什么不用 impl 在
非 core 包里达到同样效果？"*。回答不出 → 该接口/方法的归宿是 L1+。

### R2 — L1 domain 包划分

L1 包的判定：

- 只依赖 z42.core，**不依赖**别的 L1 / L2 / L3 包
- 内容形成一个**自然 domain**（collections / math / text / numerics / time / encoding / ...）
- **纯脚本**实现（无 extern，无 host FFI）
- 以"每个特性为一组类型"为单位，**单包内类型彼此互引** OK，跨 L1 包零依赖

| domain → 包 |
|---|
| 次级集合（Stack / Queue / LinkedList / PriorityQueue）→ `z42.collections` |
| 数学（Math / 复杂运算 / 几何）→ `z42.math` |
| 文本（StringBuilder / Regex / Encoding） → `z42.text` |
| 数值扩展（BigInteger / Decimal / 复数）→ `z42.numerics`（未来）|
| 日期时间（DateTime / TimeSpan）→ `z42.time`（未来；纯脚本可行性待评估）|

### R3 — L2 runtime 包

L2 包的判定：

- 依赖 z42.core（必然）+ 可选依赖其他 L2（如 threading 依赖 io 做 stderr 输出）
- 提供 **OS / runtime 服务接口**（文件、网络、线程、计时器）
- 通常需要 host FFI（z42.io 已是例外） OR 与 VM 紧密协作

| runtime → 包 |
|---|
| 文件 / 控制台 / 环境 / 进程 / 时间钟 → `z42.io` |
| 线程 / 锁 / 原子 / channel → `z42.threading`（需 L3 并发模型） |
| async / await runtime / Task → `z42.async`（需 L3 关键字 + scheduler） |

### R4 — L3 外部包

L3 包的判定：

- 依赖 L2 runtime 服务（io / threading / async）
- 提供**特定领域的高层抽象**（HTTP / JSON / LINQ / 测试框架）
- 多数纯脚本即可，少数性能敏感的可下沉到 z42.core 加 intrinsic（但需要单独 RFC）

| 高层 → 包 |
|---|
| HTTP / Socket / URL → `z42.net` |
| JSON 序列化 / 反序列化 → `z42.json` |
| LINQ 风格 IEnumerable 扩展 → `z42.linq` |
| 测试运行时（Test 注解、Assert 扩展）→ `z42.test` |
| 调试 / 计时 / 日志 → `z42.diagnostics` |

---

## 命名规则

### 包名

- 全小写：`z42.core` / `z42.io` / `z42.net.http`（多级允许，但避免过深）
- 顶级前缀始终 `z42.` —— 区分官方 stdlib vs 第三方
- 第三方包不限前缀（用户自由）

### 命名空间

- 大小写按 PascalCase，根命名空间 `Std`（与 C# `System` 对应）
- 单包**可以**用多个命名空间（罕见，需理由）
- 单命名空间**可以**跨多个包（如 `Std.Collections` 在 z42.core + z42.collections）—— 但**新代码倾向单包单命名空间**

| 包 | 命名空间 |
|---|---|
| z42.core | `Std` (primitive + protocols), `Std.Collections` (List, Dictionary) |
| z42.collections | `Std.Collections` (Stack, Queue, ...) |
| z42.io | `Std.IO` |
| z42.math | `Std.Math` |
| z42.text | `Std.Text` |
| z42.threading（未来）| `Std.Threading` |
| z42.async（未来）| `Std.Async` |
| z42.net（未来）| `Std.Net` / `Std.Net.Http` |

---

## 决策模板：新增包 RFC

任何 stdlib 新包提案必须回答以下问题（在 `spec/changes/<add-pkg>/proposal.md` 里）：

```markdown
## R1 决策树
- (A) VM intrinsic？     [yes/no + 理由]
- (B) 基础协议？         [yes/no]
- (C) 每个程序都用？     [yes/no + 频率估计]
- (D) primitive stdlib？ [yes/no]
- (E) 单 domain？        [yes/no + domain 名]
- (F) 需要 OS / runtime？[yes/no + 哪些服务]
→ **结论**：进 [z42.core / z42.<domain> / 不开新包]

## 依赖闭包
[列出本包依赖的所有上游 zpkg，必须形成 DAG，不允许圈]

## 可纯脚本化？
- 全部 .z42 ✅ / 否 ❌（说明哪些方法必须 native + 走哪条通道）

## 命名空间
- 选用：Std.<X>
- 理由：与 C# / Rust 对齐 / 现有 namespace 已饱和 / ...

## 替代方案
- 不开新包：在 z42.core 加 ... / 用 cross-zpkg impl 在现有包扩展 ...
- 选这个方案的理由：...
```

---

## 应用：当前布局复审 + 建议变更

### 复审结论

| 包 | 范围 | 建议 |
|---|---|---|
| `z42.core` | 类型 + 类型系统协议 | 🔄 **瘦身**（见下表） |
| `z42.collections` | 次级容器 + 容器策略接口 | 🔄 **接收**：IComparer<T> / IEqualityComparer<T>（从 core 迁出） |
| `z42.math` | 数学函数 | ✅ 保持；新数值 trait 走 `z42.numerics`（新包）|
| `z42.text` | 文本处理 | 🔄 **接收**：IFormattable（从 core 迁出） |
| `z42.io` | OS 服务 | ✅ 保持；Console 隐式 prelude（见末尾决策） |

### z42.core 瘦身：候选迁出清单

> **前提**：cross-zpkg `impl Trait for Type` 已可用（commit 9284e30）— primitive 类型 `int` / `double` 等在 z42.core 定义，外部包通过 `impl IXxx for int` 给它们追加 trait 实现，下游 TypeChecker 通过 IMPL section 看到。

| 当前位置 | 拟迁出到 | 影响面 | 优先级 |
|---|---|---|---|
| `z42.core/IComparer.z42` | `z42.collections/IComparer.z42` | List.Sort 用到的策略接口；用户自定义比较器 | **P1** 直接迁，无技术阻碍 |
| `z42.core/IEqualityComparer.z42` | `z42.collections/IEqualityComparer.z42` | Dictionary 用到的相等比较策略 | **P1** |
| `z42.core/IFormattable.z42` | `z42.text/IFormattable.z42` | ToString(format) 协议 | **P1** |
| `z42.core/INumber.z42` | `z42.numerics/INumber.z42` (新包) | 数值泛型代数；primitive 实现移到该包 via impl | **P2** 需要新建 z42.numerics + 把 int/double 的 op_Add 等 static override 通过 impl 块迁过去 |
| Exception **子类**（ArgumentException / FormatException / IndexOutOfRangeException 等 9 个） | 留 `z42.core/Exceptions/` | catch 频繁 + 用户期望 prelude | **保持** 不迁 |
| `z42.core/IDisposable.z42` | 留 `z42.core` | `using` 语句语法的契约（未来语法点）| **保持** |
| `z42.core/IEnumerable.z42` / `IEnumerator.z42` | 留 `z42.core` | foreach 鸭子协议 | **保持**（LINQ 扩展走 `z42.linq` 的 impl） |
| `z42.core/Collections/{List, Dictionary}` | 留 `z42.core/Collections/` | 集合本身 | **保持**（W1 决策不变；扩展方法走 impl） |
| `z42.core/IEquatable.z42` / `IComparable.z42` | 留 `z42.core` | 泛型约束 / 容器查找内核 | **保持** |

### Console 是否上提到 z42.core？

**论据 PRO（上提）**：
- 几乎每个 z42 程序都用 `Console.WriteLine`
- C# 把 `System.Console` 放在 CoreLib（虽然 .NET 拆出来过，但仍然是 prelude 体验）
- Rust 的 `println!` 是 std 内置（虽然 std 也含 io），等同 prelude 调用
- 现状很多测试代码确实直接 `Console.WriteLine` 没显式 `using Std.IO;`，说明编译器把 Console 当作隐式
- 移到 z42.core 后用户写 hello world 完全不需要任何 `using` —— 与 C# 顶级语句体验一致

**论据 CON（保持）**：
- Console 实现需要 host FFI（stdin/stdout） —— 把 host FFI 引入 z42.core 会破坏当前"VM extern 集中在 z42.core，host FFI 集中在 z42.io"的清晰边界
- 现在 `Console` 走 host FFI（不是 VM intrinsic），与 z42.core 的 `[Native(...)]` 是两套通道；上提需要 z42.core 同时承载两类 native 接口
- 拆得太细可能反而增加规范复杂度

**初步建议**（待 User 拍板）：
- **方案 A（保持现状）**：Console 留在 z42.io，但**让编译器把 `Std.IO` 加进隐式 prelude using 列表**（与 z42.core 一起自动加载）。结果：用户体验同"在 core"，物理边界仍清晰。 **推荐。**
- **方案 B（上提 z42.core）**：把 Console.z42 + 相关 `__println` / `__print` / `__readline` builtin 从 z42.io 搬到 z42.core，更新"VM extern 集中在 z42.core" 规则的措辞为"VM intrinsic + console host FFI 集中在 z42.core"。物理统一，但规则措辞需要细分。
- **方案 C（不变）**：保持现状，文档说明"Console 需要 `using Std.IO;`"（与现状不一致，需改测试代码）。 **不推荐**。

File / Path / Environment **不应**上提（不是每个程序都需要，不在 prelude 路径上）。

### Console 是否上提到 z42.core？

**论据 PRO（上提）**：
- 几乎每个 z42 程序都用 `Console.WriteLine` —— 满足 R1 (C)
- C# 把 `System.Console` 放在 CoreLib（虽然 .NET 拆出来过，但仍然是 prelude 体验）
- Rust 的 `println!` 是 std 内置（虽然 std 也含 io），等同 prelude 调用
- 现状很多测试代码确实直接 `Console.WriteLine` 没显式 `using Std.IO;`，说明编译器把 Console 当作隐式
- 移到 z42.core 后用户写 hello world 完全不需要任何 `using` —— 与 C# 顶级语句体验一致

**论据 CON（保持）**：
- Console 实现需要 host FFI（stdin/stdout） —— 把 host FFI 引入 z42.core 会破坏当前"VM extern 集中在 z42.core，host FFI 集中在 z42.io"的清晰边界
- 现在 `Console` 走 host FFI（不是 VM intrinsic），与 z42.core 的 `[Native(...)]` 是两套通道；上提需要 z42.core 同时承载两类 native 接口
- 拆得太细可能反而增加规范复杂度

**初步建议**（待 User 拍板）：
- **方案 A（保持现状）**：Console 留在 z42.io，但**让编译器把 `Std.IO` 加进隐式 prelude using 列表**（与 z42.core 一起自动加载）。结果：用户体验同"在 core"，物理边界仍清晰。 **推荐。**
- **方案 B（上提 z42.core）**：把 Console.z42 + 相关 `__println` / `__print` / `__readline` builtin 从 z42.io 搬到 z42.core，更新"VM extern 集中在 z42.core" 规则的措辞为"VM intrinsic + console host FFI 集中在 z42.core"。物理统一，但规则措辞需要细分。
- **方案 C（不变）**：保持现状，文档说明"Console 需要 `using Std.IO;`"（与现状不一致，需改测试代码）。 **不推荐**。

File / Path / Environment **不应**上提（不是每个程序都需要，不在 prelude 路径上）。

---

## 反例（什么情况下不开新包）

- **类型只有 1-3 个方法**：放在已有包里，避免 zpkg 碎片化
- **跟现有包 80% 重合的 domain**：合进现有包，用子命名空间区分（`Std.Collections.Concurrent` vs `Std.Collections`）
- **只是想分离测试**：测试不该是一个独立 stdlib 包；用 `z42.test` 框架 + 用户项目的 test 目录
- **第三方功能**：不是 z42 官方的，**不进 z42.<x>** —— 用户自己开包

---

## 与既有规范的关系

- 本文档：**规则与决策模板**（pre-write 检查清单）
- `src/libraries/README.md`：**实现规范**（Script-First / VM extern 边界 / 构建流程）—— 偏向"怎么写"
- 单包内的设计：每个 stdlib 包自己的 README（如 `z42.core/README.md`）讲该包内部的拆分、API 风格、迁移历史

---

## 后续动作（建议）

按依赖顺序排列，每条独立 spec：

### Phase 1：core 瘦身 P1（无技术阻碍，独立可做）

1. **shrink-core-comparers**：`IComparer<T>` / `IEqualityComparer<T>` → `z42.collections`
   - 改动：迁文件 + 更新 z42.core README + 受影响测试加 `using Std.Collections;`
   - 风险低（这两个接口当前是纯定义，无 implementer）
2. **shrink-core-formattable**：`IFormattable` → `z42.text`
   - 同上模式

### Phase 2：核心策略 RFC

3. **prelude-implicit-using**：编译器层把 `Std.IO`（以及未来的 `Std.Collections` / `Std.Math` / `Std.Text`）加进隐式 prelude using 列表
   - 解决 Console 体验问题（方案 A）
   - 同时让上面 P1 迁出后用户**不需要新加 using**（自动可见）
   - 是接下来一切瘦身的前置条件 —— **建议第一步做**

### Phase 3：core 瘦身 P2（依赖前置）

4. **add-pkg-numerics**：新建 `z42.numerics` 包
   - 把 `INumber<T>` 接口 + primitive 数值实现（`impl INumber<int> for int { static int op_Add ... }` 等）从 z42.core 迁过来
   - 依赖 L3-Impl2 cross-zpkg impl + primitive-as-struct 双重支持
   - 中等改动量（涉及泛型 op_Add 派发路径，需充分回归）

### 流程要求

5. **未来开新包前**：必须填 R1 决策树 + 依赖闭包，归到 `spec/changes/<add-pkg>/proposal.md`
6. 与 `libraries/README.md` 实现规范交叉引用 + 把"层级模型"加到 README（精简版）

---

## 历史决策对照

- **W1（2026-04-25）**：把 `List<T>` / `Dictionary<K,V>` 从 z42.collections **上提到** z42.core/Collections/，理由是"对齐 C# BCL"。
- **本文（2026-04-26）**：扩充 W1 的精神 —— **"core 放类型本身，扩展走 impl"**。容器**类型**进 core 不变，但所有围绕容器的策略接口（Comparer / EqualityComparer）和扩展方法（Sort / Where / Select）应通过外部包 + impl 提供，避免 core 持续膨胀。

两次决策的连续逻辑：**"核心类型在 core；非核心扩展在外部包"** 是同一原则的不同侧面，2026-04-26 的更新本质是给"非核心扩展"的部分明确了机制（cross-zpkg impl）和归宿（L1 包）。
