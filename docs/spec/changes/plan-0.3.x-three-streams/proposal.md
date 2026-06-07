# Proposal: 0.3.x 自举线（GC v1 地基 → stdlib 整理 ‖ 编译器**全自举** ‖ 反射 MVP → REPL capstone）

> 状态：📋 规划修订（**2026-06-07 重大重排**，supersede 2026-06-05 保守版）｜类型：roadmap 重排｜责任人：User + Claude
>
> **本次修订的历史**：2026-06-05 第一版把 0.3.x 定为"三主线保守版"——B 主线只做 4 个易做子系统（Lexer/Project/Driver/Parser），Sem/TC/IR/Pipeline 推 0.5.x，全自举（byte-identical）放 1.0，REPL 推 0.5.x。**2026-06-07 User 重新裁决：把全端到端自举从 1.0 拉到 0.3.x 作为本线招牌**，REPL 改为自举完成后的 capstone。下表"2026-06-07 裁决"覆盖原保守版对应条目。

## 背景

0.2.0 工程化已收尾（多平台 CI matrix + release 自动化落地；详见 [roadmap.md §0.2.x](../../../roadmap.md)）。0.3.x 重定位为 **z42 自举线**：

1. **编译器全自举**（B）：把 C# bootstrap 编译器的 **7 个项目**（`z42.{Core,Syntax,Project,Driver,Semantics,IR,Pipeline}`）逐子系统用 z42 重写，到端到端 `build` 全跑通 + 与 C# 实现 **byte-identical** 对账通过。目的是 User 明确的"**进一步验证并改进语言机制与完整度，并提性能**"——自举是语言完整度的终极 dogfood 试金石。
2. **标准库整理 + 性能**（A）："模块划分和性能都不是很好"——22 包重组 + 命名空间一致性 + 数据驱动 perf 攻坚。**先行**，给 B 自举提供稳定的包路径引用。
3. **反射 MVP**（C）：只读元数据 + `typeof`/`GetType` + Attribute reflection（从原 0.5.1–0.5.3 提前）。
4. **REPL**（capstone）：自举完成后，用 z42 原生编译器 + Rust VM interp eval-loop 提供交互式 REPL。

**GC v1** 是 A/B/C 共同前置，落在 0.3.0。

## User 已裁决

### 2026-06-07 裁决（本次重排，覆盖保守版）

| 决策点 | 裁决 |
|------|------|
| B 主线 0.3.x 范围 | **端到端全自举**（7 子系统全部，含 Sem/TC/IR/Pipeline）——不再止步 4 个易做子系统 |
| 自举写法 | **受限写法 + dogfood 补真卡点**：z42 编译器用今天能用的语言子集写（class+虚方法替代 record+match、循环替代 LINQ、异常替代 Result）；只有某缺口**真正阻断**自举时，才在 z42 里实现该特性（per [[feedback_dogfood_fill_gaps]]），**禁止 workaround**。不为自举"先把半个 L3（match/ADT/LINQ/Result）提前"。 |
| REPL | **本版本内提供**，作为自举完成后的 capstone（落 0.3.x 尾部），基于自举出的 z42 编译器 + interp eval-loop |
| 0.3.x 并行线 | **全保留**：GC v1 地基 → A ‖ B ‖ C，REPL 收尾 |
| byte-identical 自举 gate | 从原 1.0 前置**拉到 0.3.x 退出标准** |

### 2026-06-05 裁决（仍有效部分）

| 决策点 | 裁决 |
|------|------|
| C 主线 0.3.x 深度 | 只读元数据 + typeof/GetType + Attribute reflection；**不含 Method.Invoke**（强依赖 generic instantiation，推 0.5.x L3-R） |
| GC v1 时机 | 前置到 0.3.0（A/B/C 共同前置） |
| z42 编译器源码位置 | `src/z42.compiler/` 独立顶级目录，7 子包 1:1 镜像 C# 项目（详见下文 §B 主线深度规划）|

## 三主线概览

### A — 标准库整理 + 性能

**目标**：22 包模块划分梳理（合并/拆分/重命名）+ 命名空间一致性 + 公开 API 边界 + 数据驱动 perf 攻坚。

**已识别 perf 候选**（需 bench 数据确认排序）：BigInt（Karatsuba）/ List·Dict 热路径 / String·StringBuilder / Path·Encoding / JSON·YAML·TOML reader（共用 lexer 抽取）。

**已识别组织争议**（A0 spec 裁决）：Console placement（z42.io vs z42.core prelude）/ 命名空间一致性（`Std.IO.Binary` vs `Std.IoBinary`）/ 可合并·可拆分包。

**与 B 的耦合**：A1 重组**先于** B 的子系统大规模铺开，让自举编译器引用的 stdlib 包路径稳定，避免重组冲击 in-flight 的 B 代码。

### B — 编译器全自举（本版本招牌）

**目标**：7 个子系统全部用 z42 重写 + **逐子系统 byte-identical CI gate** + end-to-end compile-perf gate（median ≤ 3× C# / P99 ≤ 5×）。

**7 子系统 → z42.compiler 子包**（依赖序）：

| # | C# 项目 | → z42 子包 | kind | L1/L2 能写? | 难点 / 真卡点候选 |
|:-:|--------|-----------|:----:|:----:|------|
| 1 | z42.Core | core | lib | ✅ | 共享工具类型 |
| 2 | z42.Syntax | syntax | lib | ✅ | Lexer 字符迭代（z42.text 已够）；Parser AST 用 class+虚 visitor 替代 record |
| 3 | z42.Project | project | lib | ✅ | z42.toml reader 已有 |
| 4 | z42.Driver | driver | exe | ✅ | z42.cli arg parsing 已有；CLI 错误码格式化 |
| 5 | z42.Semantics | semantics | lib | ⚠️ | 绑定/符号解析；visitor 重度——**首个可能 dogfood 出语言缺口的子系统** |
| 6 | z42.IR | ir | lib | ⚠️ | codegen + lowering；寄存器 SSA 构造 |
| 7 | z42.Pipeline | pipeline | lib | ⚠️ | 编排上述全部 → 端到端 `build` |

**受限写法约定**（B0 spec 固化，全子系统遵守）：

- **AST / IR 节点**：`class` 继承层级 + `virtual` 方法 dispatch，**不**用 record + `match`（match 排 0.7.x）
- **集合变换**：`for`/`foreach` + 显式累积，**不**用 LINQ（排 0.6.x）
- **错误路径**：`throw`/`try-catch` + Exception 子类，**不**用 `Result<T,E>` + `?`（排 0.7.x）
- **泛型**：用已落地的 G1-G4 + 闭包核心；遇关联类型（G3a）等延后特性 → 视为真卡点处理

**dogfood 缺口处理**（贯穿 B 全程）：自举写到某处发现今天的 z42 子集**真正写不出来**（不是"不优雅"而是"无法表达"）时——停下汇报 → 判定是 L1/L2 可补 / 还是必须拉 L3 特性 → 若 L1/L2 可补则当次 spec 实现；若必须 L3 则在 features.md 评估是否值得为自举提前该特性，**禁止在编译器代码里写绕过**。

**byte-identical CI gate（0.3.x 退出标准，非 1.0）**：每个 `z42.compiler.<sub>.zpkg` 的产物输出（token stream JSON / AST JSON / manifest 解析结果 / .zbc / .zpkg 字节）与对应 C# `z42.<Sub>.dll` 逐字节对账。全 7 子系统 7 日零飘移 = B 主线达标。

详细目录布局 / workspace / CLI parity / perf gate / 1.0 切换路径见文末 **§B 主线深度规划**（沿用 2026-06-06 决议，仅把"4 子系统"扩到"7 子系统"、把"byte-identical 推 1.0"改为"0.3.x 退出"）。

### C — 反射 MVP

**0.3.x 范围**（不变）：

- C0 Spec：`Type`/`MethodInfo`/`FieldInfo`/`PropertyInfo`/`ParameterInfo` API 形状 + 与 zpkg `TypeDesc`/`MethodDesc` 元数据映射（**新建 `docs/design/language/reflection.md`**，当前无此文档）
- C1：runtime 暴露 `Type.GetMembers/GetMethods/GetFields/GetProperties` 系列
- C2：`typeof(T)` 编译器关键字 + `obj.GetType()` runtime intrinsic + z42.reflection 包公开
- C3：Attribute reflection（前置：用户自定义 attribute 机制 spec 需先落地）

**0.3.x 不做**（推 0.5.x L3-R 完整版）：`Method.Invoke` / `Activator.CreateInstance<T>()` / `Type.MakeGenericType`。

**与 B 的协同**：自举编译器自身需要读类型元数据（Semantics/TypeChecker），C 主线暴露的 metadata API 正是 B 的客户——两线互为 dogfood。

### REPL — capstone（自举完成后）

自举端到端 `build` 跑通后，提供交互式 REPL：

```
.z42/z42 repl
> var x = 42
> x * 2
84
> class Point { int X; int Y; }
> new Point { X = 1, Y = 2 }
Point { X = 1, Y = 2 }
```

依赖（全部由 B 主线在 0.3.x 内交付）：z42 原生 Semantic + TypeChecker + IR + 持久化符号表 + interp eval-loop（复用）+ 跨 line scope 设计。单独 spec `add-z42-repl`，落 0.3.x 尾部。

> **与 2026-06-05 保守版的差异**：原版因"REPL 需 z42 自举的 Semantic/TypeChecker，而那些推 0.5.x"把 REPL 推 0.5.x。本次全自举把 Semantic/TypeChecker 拉进 0.3.x，REPL 的前置在本线内满足，故 REPL 回到 0.3.x。

## 0.3.x 子版本编排（草案，子版本号弹性——本线跑到全自举 + byte-identical 为止）

```
0.3.0   GC v1（mark-and-sweep 替换 Rc<RefCell>）—— A/B/C 共同前置

0.3.1   规划 + 三主线 spec 起草：
        A0 包审计 spec / B0 自举架构 spec（含受限写法约定 + 7 子系统计划）/ C0 反射 API spec
        + 创建 src/z42.compiler/ workspace 骨架（7 子包占位 + xtask build/test compiler-z42）

0.3.2   A1 包结构重组（先行，稳定 B 引用的包路径）

0.3.3   B: core + syntax（Lexer+Parser+AST，受限写法）+ bit-identical gate
        ‖ A2 bench baseline（每包 micro-bench → bench-baselines）
        ‖ C1 runtime metadata 暴露 + 4 反射对象类型 + GetMembers 系列

0.3.4   B: project（manifest reader）+ driver（CLI lex/parse/manifest-check）
        ‖ A3 perf #1（BigInt Karatsuba + List/Dict 热路径）
        ‖ C2 typeof(T) + obj.GetType() + z42.reflection 包公开

0.3.5   B: semantics（绑定/符号解析）—— 首个硬子系统，dogfood 缺口高发段
        ‖ A4 perf #2（String/StringBuilder/Path/Encoding）
        ‖ C3 Attribute reflection（前置：attribute 机制 spec 于 0.3.4 起草）

0.3.6   B: typecheck ‖ A5 perf #3（JSON/YAML/TOML reader，共用 lexer 抽取）

0.3.7   B: ir（codegen + lowering，寄存器 SSA 构造）

0.3.8   B: emit（ZbcWriter/ZpkgWriter）—— 产 byte-identical .zbc/.zpkg

0.3.9   B: pipeline（编排）→ 首个 z42 端到端 build 跑通

0.3.10  B: byte-identical gate 全 7 子系统绿（z42c-selfhost 编 stdlib + 自身源码字节相同）
        + compile-perf gate 启用（median ≤ 3× / P99 ≤ 5×）

0.3.11  REPL capstone（z42 原生）

0.3.12  收尾：z42c-selfhost 下全部 dotnet test / xtask test 全绿 + soak + A perf delta report
```

`‖` = 同子版本三主线并行推进。

> **子版本号弹性声明**：上表是当前可见的最小拆分；自举实际推进中若某子系统（尤其 semantics/ir）触发 dogfood 补特性，会插入特性 spec 子版本。本线的**终点由退出标准定义**（见下），不由子版本号定义。

## SemVer 路线影响（需同步重排 roadmap.md 长期表）

把全自举从 1.0 拉到 0.3.x，连锁影响：

| 原排期 | 原内容 | 重排后 |
|------|------|------|
| 0.5.x B | 自举剩余子系统（Sem/TC/IR/ZbcWriter/Pipeline）| **并入 0.3.x** |
| 1.0 | 自举 byte-identical 替换 | **核心拉到 0.3.x**；1.0 仅保留"删 C# bootstrap + 跨架构 NativeAOT + SemVer 启用"等收尾 |
| match/ADT/LINQ/Result（0.6/0.7）| 完整 L3 函数式/错误处理 | **维持原版本**，除非自举 dogfood 判定某特性为真卡点才提前（按 features.md 逐特性评估）|
| REPL（原 0.5.x）| z42 原生 REPL | **拉到 0.3.x capstone** |

> **关键边界**：受限写法决策保证全自举**不**强制提前整个 0.6/0.7。只有被自举真正阻断的单个特性才按需提前；match/LINQ/Result 的"完整版"仍在 0.6/0.7，供用户代码用更优雅的写法。

## 退出标准（GREEN）

- **A 主线**：22 包审计 spec 落地 + 重组完成 + 每包 bench baseline + 三轮 perf 攻坚 delta report（bench-baselines 分支）
- **B 主线**：7 子系统全部 z42 实现 + byte-identical gate 7 日零飘移 + end-to-end compile-perf median ≤ 3× C# + z42c-selfhost 下全部既有测试全绿
- **C 主线**：z42.reflection 包公开 + 4 反射对象类型 + GetMembers 系列 + typeof/GetType + Attribute reflection；MVP 单测覆盖 ≥ 90%
- **REPL**：交互式 eval 跑通（变量/表达式/类型声明/实例化）+ 跨 line scope + 基础错误恢复

## Out of Scope（明确不在 0.3.x）

- `Method.Invoke` / generic instantiation 反射（→ 0.5.x L3-R）
- match/ADT/LINQ/Result **完整版**（→ 0.6/0.7；除非自举真卡点逐特性提前）
- async/await（编译器是同步的，自举不需要）
- 删除 `src/compiler/` C# bootstrap（→ 1.0；0.3.x 期间两实现并存，default 仍是 C#）
- NativeAOT 跨架构（→ 1.0）

## Open Questions（spec 起草阶段需 User 裁决）

1. **A0**：22 包合并/拆分提案（package-by-package 裁决）+ Console placement 最终方案
2. **B0**：byte-identical CI gate 触发频率（每 PR / 每日 / 每 commit）
3. **B0**：受限写法的 AST/IR 表达——class 层级 + 虚 visitor 的具体抽象形态（参考 [[D-11]] introduce-bound-visitor）
4. **B0**：自举编译器子包的 zpkg 产物路径 + xtask build/test compiler-z42 dispatch 细节
5. **C0**：`Type` 对象生命周期（cached / per-call new）+ 与 GC v1 的交互
6. **C3 前置**：用户自定义 attribute 机制 spec 范围（仅 [Test] / 通用用户定义 / 何种语法）
7. **dogfood 升级判据**：自举遇缺口时，"L1/L2 可补"与"必须拉 L3"的判定标准如何成文（避免每次主观裁决）

---

**审批状态**：2026-06-07 User 已裁决四项重排（全自举 / 受限写法 / REPL 本版 / 全保留）；本 proposal 反映该裁决，待 User 确认子版本编排后同步 roadmap.md，再于 0.3.1 启动 A0/B0/C0 三独立 change spec。

---

## §B 主线深度规划（编译器自举）

> 沿用 2026-06-06 User 决议（目录布局 / workspace / 无桥接策略 / perf gate / 1.0 切换），本次仅两处扩展：① "4 子系统"→"7 子系统全自举"；② "byte-identical 推 1.0"→"0.3.x 退出标准"。

### 1. 目录布局（独立顶级目录 + 7 zpkg 子包，镜像 C# 项目）

**位置：`src/z42.compiler/`** 独立顶级目录（2026-06-06 决议），与 `src/compiler/`（C# bootstrap）平级。`src/libraries/` 保持纯净只放 stdlib。

```
src/
├── compiler/                                ← C# bootstrap 编译器（现状，0.3.x 仍 default）
├── z42.compiler/                            ← 新增顶级目录
│   ├── z42.workspace.toml                   ← 独立 workspace（与 stdlib 解耦）
│   ├── README.md
│   ├── core/        → z42.compiler.core.zpkg      (lib)
│   ├── syntax/      → z42.compiler.syntax.zpkg    (lib)   ← Lexer + Parser + AST
│   ├── project/     → z42.compiler.project.zpkg   (lib)   ← manifest reader
│   ├── driver/      → z42.compiler.driver.zpkg    (exe)   ← CLI 入口 = z42c.zpkg
│   ├── semantics/   → z42.compiler.semantics.zpkg (lib)   ← 0.3.5（原占位，现实做）
│   ├── ir/          → z42.compiler.ir.zpkg        (lib)   ← 0.3.7（原占位，现实做）
│   └── pipeline/    → z42.compiler.pipeline.zpkg  (lib)   ← 0.3.9（原占位，现实做）
├── runtime/
├── libraries/                               ← 22 stdlib 包（A 主线重组）
└── toolchain/
```

> **与保守版差异**：semantics/ir/pipeline 三子包从"空壳占位、0.5.x 启动"改为"0.3.5/0.3.7/0.3.9 实做"。

**包间依赖图**（与 C# 项目引用一致）：

```
core ◄── syntax    ◄── pipeline ◄── driver
core ◄── project   ◄── pipeline
core ◄── semantics ◄── pipeline
core ◄── ir        ◄── pipeline
```

driver 是唯一 exe-kind；其余 6 个是 lib-kind。**用户调用入口**：`z42c.zpkg` = `z42.compiler.driver.zpkg` 对外别名。

**独立 workspace**（`src/z42.compiler/z42.workspace.toml`，0.3.1 创建）：`members = ["*"]` 自动发现，`[workspace.project].version = "0.1.0"`（与 stdlib 独立 versioning）。**stdlib workspace 不动**。

**xtask 扩展**（0.3.1）：`xtask build/test compiler-z42` + `xtask build all` 级联（runtime + compiler + stdlib + compiler-z42）。zero C# 编译器改动——纯新增 dispatch。

### 2. CLI parity（无桥接策略，2026-06-06 不变）

z42 端只 ship 已就绪命令，**绝不**调 dotnet z42c.dll 作 fallback。0.3.x 推进中逐步解锁：

```
0.3.4 起：lex / parse / manifest-check（syntax + project 就绪）
0.3.9 起：build（pipeline 就绪 → 端到端）
0.3.10 起：build 产物与 C# 实现 byte-identical
```

0.3.x 期间 default 编译器仍是 C# 实现；z42c-selfhost 通过 `Z42_COMPILER=selfhost` opt-in soak。两实现完全独立，对 PR 干扰为零。

### 3. Perf gate（量化定义，2026-06-06 不变）

**最终目标**：z42c-selfhost 在 z42-JIT 下编译同一 corpus 的 wall time ≤ **3× dotnet z42c.dll**（.NET JIT）。

- **0.3.3–0.3.9（铺设期）**：per-subsystem micro-bench（lexer MB/s / parser nodes/s / manifest ops/s），baseline 入 bench-baselines，不设硬阈值
- **0.3.10 起（pipeline 就绪）**：end-to-end compile-perf bench + CI gate（median ≤ 3.0 红线 / P99 ≤ 5.0 黄线 / 回归 > 15% 红线）

### 4. byte-identical gate（0.3.x 退出标准）

逐子包对账（每 `z42.compiler.<sub>.zpkg` 产物 vs 对应 C# `.dll` 产物，逐字节）。全 7 子系统 7 日零飘移 → B 主线达标。**这是 0.3.x 退出条件**（保守版里它是 1.0 前置）。

### 5. 1.0 切换路径（收窄）

0.3.x 完成全自举 + byte-identical 后，1.0 仅剩：

```bash
git rm -r src/compiler/                  # 删 C# bootstrap（待 byte-identical regression 跑稳）
# launcher 内置 z42c 短命令 → z42.compiler.driver.zpkg
```

+ 跨架构 NativeAOT + SemVer/deprecation 启用。**自举核心本身在 0.3.x 完成**。

### 6. dogfood 反馈循环（贯穿 0.3 全程）

| 发现类型 | 处理方式 |
|--------|--------|
| 缺 stdlib API | → 进 A 主线当次 spec；**禁止 workaround**（[[feedback_dogfood_fill_gaps]]）|
| 语言机制真卡点（无法表达） | 停下汇报 → L1/L2 可补则当次 spec；必须 L3 则 features.md 逐特性评估是否为自举提前 |
| 性能 hotspot（VM/GC/JIT）| → 进 perf bench；触发 runtime spec |
| 错误/异常路径不够 | → A 主线范围；增补 Exception 子类 |
