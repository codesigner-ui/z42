# z42 标准库模块划分规则

> **目的**：给出 z42 stdlib 包（zpkg）划分的可重用规则，参考 C# BCL + Rust std 实践，
> 让后续添加新包（z42.threading、z42.json、z42.linq 等）有明确的依据。
>
> **受众**：stdlib 设计者、新包提案者、reviewer。
> **不是**：用户使用文档（用户视角看 `docs/design/language-overview.md`）。

---

## TL;DR — 核心规则

1. **每个 zpkg 是分发单元，对应 `[project] name = "z42.<domain>"`**；命名空间可跨 zpkg。
2. **z42.core 是隐式 prelude** — 所有程序自动加载 + 不可声明依赖。每个 z42 程序都用得到的类型在这里。
3. **VM extern 只能在 z42.core**（lock-in 规则，见 `libraries/README.md`）；`z42.io` 是仅有的 host FFI 例外（OS 能力走单独通道）。
4. **包按层叠层分类**（L0 → L1 → L2 → L3），上层依赖下层，**禁止反向依赖**。
5. **跨包扩展走 `impl Trait for Type`** — 不必把 trait 实现挤回 trait 定义所在的包（L3-Impl2 已支持）。
6. **每个新包必须明确层级 + 依赖闭包 + 是否可纯脚本化**；不满足全部的不开新包，留 backlog。

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
  (B) 是基础协议（Object / Equals / GetHashCode / Compare 类的 trait）  → ✅ 进 z42.core
  (C) 几乎每个 z42 程序都直接用到（List / Dictionary / Exception）  → ✅ 进 z42.core
  (D) 是 primitive 类型的 stdlib 表示（struct int / class String）  → ✅ 进 z42.core
  (E) 仅特定 domain 用到（Stack / Queue / StringBuilder / Math）  → ❌ 进 L1 domain 包
  (F) 需要 OS / 运行时服务（File / Console / Thread）  → ❌ 进 L2 包
否则 → 不要进 z42.core
```

> **设计目标**：z42.core 必须能稳定 build → 是所有其他包的依赖底座。任何引入新依赖
> 关系或破坏 prelude 体验的类型，宁可放 L1 也不放 z42.core。

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

| 包 | R1/R2/R3 评估 | 建议 |
|---|---|---|
| `z42.core` | (A)(B)(C)(D) 全 hit | ✅ 保持 |
| `z42.collections` | (E) Stack/Queue 是次级集合 | ✅ 保持；未来加 LinkedList / PriorityQueue 在此 |
| `z42.math` | (E) domain="数学" | ✅ 保持；可考虑未来 numerics 拆分（BigInteger / Complex）|
| `z42.text` | (E) domain="文本" | ✅ 保持；StringBuilder Script-First 迁移已完成 |
| `z42.io` | (F) OS 服务 | ✅ 保持，但 **Console 应考虑上提到 z42.core**（见下） |

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

1. **本文 review** → User 决策 Console 上提与否（方案 A/B/C）
2. **方案落地**（任选）：
   - A：补一行 implicit using 规则到 prelude
   - B：迁移 Console.z42 + builtin → z42.core
3. **未来开新包前**：必须填 R1 决策树 + 依赖闭包，归到 `spec/changes/<add-pkg>/proposal.md`
4. 与 `libraries/README.md` 实现规范交叉引用 + 把"层级模型"加到 README（精简版）
