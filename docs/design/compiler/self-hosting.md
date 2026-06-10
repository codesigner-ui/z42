# 编译器自举（self-hosting）— `src/z42c/` 架构

> 状态：🚧 进行中（0.3.x B 主线）｜起点：B0 [scaffold-z42c-selfhost](../../spec/changes/scaffold-z42c-selfhost/)（2026-06-07）
>
> 本文是 z42 自举编译器的**唯一权威架构文档**：布局、受限写法、构建解析、对账策略、CLI parity、1.0 切换。规划背景见 [`roadmap.md` 0.3.x](../../roadmap.md) + [`plan-0.3.x-three-streams`](../../spec/changes/plan-0.3.x-three-streams/proposal.md)。

## 目标

把 C# bootstrap 编译器（`src/compiler/`，7 个项目）**逐子系统用 z42 重写**到端到端 `build` 跑通 + 与 C# 实现 **byte-identical** 对账通过。目的：用最严苛的 dogfood 验证并改进语言机制与完整度，并提编译性能。

**核心边界**：0.3.x 期间 **default 编译器仍是 C#**；z42c-selfhost 两实现并存、逐字节对账，对既有 PR 零干扰。删除 C# bootstrap 留到 1.0。

## 目录布局

`src/z42c/` 独立顶级目录（与 `src/compiler/` C# bootstrap 平级），独立 workspace（与 `src/libraries/` stdlib 解耦）：

```
src/z42c/
├── z42.workspace.toml          # members=["*"]，输出 artifacts/build/z42c/<pkg>/<profile>/
├── z42c.core/      → z42c.core.zpkg      (lib)   镜像 z42.Core
├── z42c.ir/        → z42c.ir.zpkg        (lib)   镜像 z42.IR
├── z42c.syntax/    → z42c.syntax.zpkg    (lib)   镜像 z42.Syntax   (Lexer+Parser+AST)
├── z42c.project/   → z42c.project.zpkg   (lib)   镜像 z42.Project  (manifest reader)
├── z42c.semantics/ → z42c.semantics.zpkg (lib)   镜像 z42.Semantics(TypeCheck+Codegen)
├── z42c.pipeline/  → z42c.pipeline.zpkg  (lib)   镜像 z42.Pipeline (编排)
└── z42c.driver/    → z42c.driver.zpkg    (exe)   镜像 z42.Driver   (= z42c 入口别名)
```

**目录名 == `[project].name` == zpkg basename**（如 `z42c.core`），与 stdlib 约定一致：member 逻辑名（WS001 / default-members）、`${member_name}` 模板、产物名三者重合，消除歧义。命名空间镜像 C#：`Z42.Core` / `Z42.Syntax` / `Z42.IR` / `Z42.Project` / `Z42.Semantics` / `Z42.Pipeline` / `Z42.Driver`。

**依赖图**（镜像 [`src/compiler/README.md`](../../../src/compiler/README.md) 邻接表）：

```
core ──(无依赖)        ir ──(无依赖)
syntax    ◄── core
project   ◄── ir
semantics ◄── core, syntax, ir
pipeline  ◄── core, syntax, semantics, ir, project
driver    ◄── pipeline, ir, core
```

> **目录名 vs 产物名**：byte-identical 目标是 **z42c 编译用户代码产出的 .zbc/.zpkg 字节** 与 C# z42c 一致，**不**要求 z42c 自身源码/内部名与 C# dll 相同。镜像命名纯为 1:1 可维护性。

## 受限写法约定（全子系统遵守）

用今天能用的语言子集写，**不**为自举强制提前半个 L3：

| 维度 | 用 | 不用（排期）|
|------|----|------|
| AST / IR 节点 | `class` 继承层级 + `virtual` / 抽象 `Visitor` 基类 dispatch | record + `match`（0.7.x）|
| 集合变换 | `for` / `foreach` + 显式累积 | LINQ（0.6.x）|
| 错误路径 | `throw` / `try-catch` + Exception 子类 | `Result<T,E>` + `?`（0.7.x）|
| 泛型 | 已落地 G1-G4 + 闭包核心 | 关联类型 G3a 等（按真卡点评估）|

**dogfood 缺口处理**：写到某处今天的 z42 子集**无法表达**（不是不优雅）→ 停下汇报 → 判定 L1/L2 可补 / 必须拉 L3 → L1/L2 当次 spec 实现；必须 L3 则 features.md 逐项评估是否为自举提前。**禁止在编译器代码里写 workaround**（[[feedback_dogfood_fill_gaps]]）。

**受限写法补充（实做中发现，均沿用 stdlib 既有模式，非 workaround）**：

| 发现 | C# 写法 | z42 受限写法 | 依据 |
|------|--------|-------------|------|
| **无 `enum` 关键字** | `enum DiagnosticSeverity { ... }` | `static class` + `int` 常量 | stdlib `SplitOptions` / `SeekOrigin` / `FileMode` |
| **无交错数组 `int[][]`** | `var remaps = new int[n][];` | 循环内按需重算（如 zpkg remap 借 `Intern` 幂等性）或平铺单数组+偏移 | ZpkgWriterZ._buildMods |
| **`new T[n]` 不能在实参位置** | `F(new string[1], ...)` | 先提升局部变量再传 | zpkg_tests |
| **`fn` / `module` 是保留字** | 参数/变量名随意 | 改名（fname / irm） | 多处 |
| **类字段不能带泛型参数**（`private List<X> f;` 的 `<X>` 被 parser 静默丢弃 → 取元素退化为无约束 `T`，无法调其方法）| `List<Diagnostic> _items` | **typed array + count**：`Diagnostic[] _items; int _count;` + 手动 `Grow()` | stdlib `TomlValue._arrayItems` / `Process._args`（typed array 元素访问会正确单态化）|
| **`List<T>` 过度约束** `where T: IEquatable<T> + IComparable<T>`（`IComparable` 对 Token/AST 无意义）| `List<T>` 任意元素 | 用 typed array 规避（同上）；确需有序/查找集合时按需实现接口或 `Sort(comparer)` | List.z42 约束注释 |

> 这三条决定编译器内部**集合一律用 typed array + count 并行数组**（而非 `List<T>` 字段）。`enum` / 泛型字段 / List 约束放松若后续作为独立语言增强落地，编译器代码随之迁移。

## 构建与依赖解析

**构建**：`z42c build --workspace --release`（cwd=`src/z42c`）→ 拓扑序编译 7 子包 → `artifacts/build/z42c/<member>/<profile>/{dist,cache}/`。经 xtask：`z42 xtask.zpkg build compiler-z42`。

**workspace 兄弟包解析（dogfood #1 根因修复，2026-06-07）**：

- **问题**：C# 编译器的 `BuildLibsDirs` 曾把 `artifacts/build/libraries/` 硬编码为唯一会扫描的 workspace 布局根。stdlib 兄弟依赖能解析纯因 stdlib 恰好输出到那里；z42c 输出到 `artifacts/build/z42c/`，兄弟包扫不到。
- **修复**：`WorkspaceBuildOrchestrator` 收集**本 workspace** 全体成员的 `EffectiveDistDir`（排序去重）→ 透传 `CompileMember` → `RunResolved` → `BuildTarget` → `BuildLibsDirs`，在既有扫描后**按规范化 full-path 去重追加**。
- **效果**：成员从**当前 workspace** 解析其 **toml 声明的**兄弟依赖（`declaredDeps` 过滤未声明项），与输出位置无关。stdlib 等已落在被扫描根的 workspace 去重后**零新增、顺序不变 → 零字节漂移**；单工程构建（`workspaceLibDirs=null`）行为不变。
- **规则**：除 stdlib（toolchain 自带、自动可用）外，**其他依赖必须在 toml `[dependencies]` 声明**才可解析。远程 / 下载依赖（registry / git URL）暂不支持——见 [Deferred](#deferred--future-work)。

详见 [compiler-architecture.md](compiler-architecture.md) 对应段。

### 运行 / 测试 z42c（单一 flat libs 目录 — 重要）

**运行期 `Z42_LIBS` 是单个目录（非 colon-list），且必须含全部依赖 zpkg。** 跑 z42c 产物
（driver / 测试）时，先把「z42c 7 包 + stdlib」**合并到一个 flat 目录**（`xtask test
compiler-z42` 自动组装 `artifacts/build/z42c/alllibs/<profile>/`），再 `Z42_LIBS=<该目录>`：

```
Z42_PORTABLE_VM=<z42vm> Z42_LIBS=<flat 含 z42c.*+z42.*> z42vm z42c.driver.zpkg
```

> **踩坑记录（2026-06-07）**：误把 `Z42_LIBS` 当成 colon-list 传多个目录 → 运行期当成单个
> 非法路径 → 回退到仅 stdlib → z42c 包未加载 → `VCall: function not found` + 静态字段读
> null。一度误判为 VM/编译器跨包 bug，实为 harness 配置。**跨包方法调用与静态字段在运行
> 时完全正常**（driver 端到端 + z42c.core 7/7 单测验证），7 包架构运行时成立。

### 测试通道（z42c [Test]）

测试单元布局：`src/z42c/<member>/tests/<unit>/{<name>.z42.toml(kind=lib) + *.z42}`。
`xtask test compiler-z42` = build 7 子包 smoke + 组装 flat 目录 + 逐单元 `z42c build <toml>
--release`（Z42_LIBS=flat）+ `z42-test-runner <zpkg>`（Z42_PORTABLE_VM + Z42_LIBS=flat）。
`z42.test` 是 stdlib 自动可用（**不**在 toml 声明，避免 WS013）。z42c-selfhost 仍为
opt-in soak，不入默认 GREEN gate。

## CLI parity（无桥接）

z42c.driver 只 ship 已就绪命令，**绝不** fallback 到 dotnet z42c.dll。逐子版本解锁：

| 起始 | 命令 |
|------|------|
| B0 | 仅 banner（无命令）|
| 已落地 | `--dump-tokens` / `--dump-ast` / `--dump-bound` / **`--emit-zbc <src> <out>`**（首个产物命令）|
| 已落地 | **`build <toml>`**（packed zpkg 端到端：源发现→编译→组装→写出；e2e：产物 z42vm 直跑。TSIG/IMPL 待 follow-up → 全段 byte-identical）|
| 0.3.10 | `build` 产物与 C# byte-identical |

## .zbc 写入器（z42c.ir/BinaryFormat/，port-z42c-zbc-writer 已落地）

自举 ZbcWriter 镜像 C# `z42.IR/BinaryFormat/`，**功能完整**（const[int/f64 IEEE754/bool/null/str]/算术/比较/位/一元/控制流/调用 token/对象/字段/数组/is·as/字符串拼接 → 全 8-section .zbc，z42vm 直接执行）：

- **架构**：`ByteWriter`（int[] 0..255 + LE 助手，规避 byte 型）→ `ZbcInstr`（集中 if-is 编码）→ `ZbcWriter`（intern 预扫 + 8-section 组装）；`TokenAllocator`（插入序 index；跨模块 `ImportBase(1<<31)|STRS idx`）；`ZbcStringPool`（插入序 = STRS 字节序）。
- **确定性铁律**：字符串池 intern 序须 1:1 复刻 C# `InternPoolStrings`（模块名 → const.str 池 → 类 → "?" → 每函数[名/ret/param → 每块 label→指令串]）；IMPT 写前 Ordinal 排序。
- **验证双轨**：① `empty` fixture 逐字节对账（版本随 C# bump 同步，见 version-bumping.md 第 5 步）；② **e2e 执行验证**（xtask `test compiler-z42` 的 e2e 步：自检程序经 `z42c --emit-zbc` → z42vm 执行，div-by-zero oracle + 负向用例）。
- **受限/缺口**：runtime builtin 直接 `[Native("__double_to_bits")]` 自声明（免 stdlib 改动）；ctor-less 类合成 `"Class.Class"` ctor token（VM 查无即跳）；char 字面量待前端整链；**DBUG 已落地**（add-z42c-source-spans：AST/Bound 全节点携 Span → TrackLine[每语句,同行去重,file basename] → IrFunction.LineTable/LocalVarTable[RegId 序] → DBUG 第 9 section + HasDebug flag）。

## byte-identical 对账（0.3.x 退出标准）

每个 `z42c.<sub>.zpkg` 的产物（token stream JSON / AST JSON / manifest 解析 / .zbc / .zpkg 字节）与对应 C# `z42.<Sub>.dll` 产物逐字节对账。全 7 子系统 7 日零飘移 = B 主线达标。

- **现状**：`.zbc` 对账已上线 **per-construct byte-identical**——`xtask test compiler-z42` 的 e2e byte-compare 步把 3 个真实程序（算术/控制流、调用/对象/字段/字符串、浮点/is·as/继承）经 z42c 与 C# 同源编译逐字节 diff（含 DBUG）。已知 lowering parity 约定：int 字面量恒 ConstI64 / str `+`=Add(tag str) / while 块 label cond_·body_·end_ / 无基类→Std.Object / 函数序=类方法→自由函数 / var-decl=专用寄存器+copy（详见 archive/2026-06-10-add-z42c-source-spans tasks 实施记录）。
- **不纳入默认 GREEN gate**：z42c-selfhost 是 0.3.x 期间 opt-in soak（`z42 xtask.zpkg test compiler-z42`），既有 `z42 xtask.zpkg test` 不含它。

## compile-perf gate

最终目标：z42c-selfhost（z42-JIT）编译同一 corpus wall time ≤ 3× dotnet z42c.dll（median ≤ 3.0 / P99 ≤ 5.0）。0.3.3–0.3.9 铺设期 per-subsystem micro-bench 入 bench-baselines、不设硬阈值；0.3.10 起 end-to-end gate 启用。

## 1.0 切换路径

0.3.x 完成全自举 + byte-identical 后，1.0 仅剩 `git rm -r src/compiler/`（待 regression 跑稳）+ launcher 内置 `z42c` 短命令指向 `z42c.driver.zpkg` + 跨架构 NativeAOT + SemVer 启用。

## Deferred / Future Work

### self-hosting-future-remote-deps

- **来源**：B0 scaffold-z42c-selfhost（2026-06-07 User 裁决）
- **触发原因**：现无整体包管理；workspace 兄弟解析先只支持「从当前 workspace 找本地项目」
- **前置依赖**：registry / git-URL 依赖来源 + 版本求解 + 下载缓存机制的整体设计
- **触发条件**：跨 workspace / 第三方包分发需求出现时
- **当前 workaround**：所有非 stdlib 依赖必须是同 workspace 的本地 member 且在 toml 声明
