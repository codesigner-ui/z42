# z42 Roadmap

> **本文档 = z42 唯一的迭代计划**：当前焦点、下一阶段、长期 SemVer 路线、未完成项索引。
>
> **已完成**：每个落地的功能对应一个 `docs/spec/archive/YYYY-MM-DD-<name>/` 归档目录（带完整 proposal / design / tasks / 实施备注）；本文不复述。需要查"X 何时落地、为什么这样设计"按主题或日期检索 [`docs/spec/archive/`](spec/archive/) 即可。
>
> **设计决策**：见 [`docs/features.md`](features.md)（决策 + 理由 + phase 归属）+ [`docs/design/philosophy.md`](design/philosophy.md)（顶层哲学）。
>
> **实施细节**：见 [`docs/design/`](design/) 5 个主题子目录。

---

## 设计目标

z42 是一门**全栈系统编程语言**：从嵌入式固件到云端后端，无需切换语言。融合 C# 语法 + Rust 纪律 + Python 易用性。

| 维度 | 设计 |
|------|------|
| 语法 | C#（命名 / 声明 / OOP 结构）|
| 内存 | 始终 GC（无所有权 / 借用 / 生命周期）|
| 错误处理 | L1 异常 / L3 引入 `Result<T, E>` + `?`（共存）|
| 类型系统 | 静态类型 + 局部推断 + 泛型 + 接口；L3 引入 Trait 静态分发 |
| 执行模型 | Bytecode-native：Interp / JIT / AOT 三模式，命名空间级 `[ExecMode]` 注解切换 |
| 嵌入 | VM 设计为可嵌入到外部 app（C ABI），目标 ~200KB 子集 |
| 互操作 | 三层 ABI（C / Rust ergonomic / 平台 facade），native 类型可注册进 z42 |

性能基线（philosophy §9）：interp ≤ Python 1.5×；JIT ≥ V8 70%；AOT ≥ Go 80%；GC pause < 5ms p99；嵌入子集 < 200KB。

---

## 固定决策

- **GC**：z42 始终带 GC，永不引入所有权 / 借用（降低上手成本）
- **IR**：寄存器 SSA 形式
- **执行模式注解**：作用于命名空间级
- **`.zbc` magic**：`ZBC\0`
- **pre-1.0 不承诺向后兼容**（与 [`philosophy.md` "不为旧版本提供兼容"](../.claude/rules/philosophy.md#不为旧版本提供兼容2026-04-26-强化) 对齐）
- **1.0+ 启用 SemVer + deprecation 周期**

---

## 阶段总览

| 阶段 | 目标 | 状态 |
|------|------|:----:|
| **L1** | C# 基础子集，跑通完整 pipeline（源码 → IR → VM 执行） | ✅ 已完成 |
| **L2** | 基础设施（编译、工程、测试、VM 质量、标准库） | 🚧 进行中 |
| **L3** | 高级语法（泛型 / Lambda / async + z42 特有特性） | 🟡 部分（泛型 + lambda + delegates 提前落地）|

阶段串行：L1 全通 → L2；L2 全完成 → L3。当前 L1 全绿、L2 多项进行中、L3 部分提前落地。

---

## 当前焦点

**0.3.x 自举线**：以 GC v1 为地基，三主线并行——A（stdlib 重组 + perf）‖ B（**编译器全自举**：7 子系统用 z42 重写到 byte-identical）‖ C（反射 MVP），REPL 作为自举完成后的 capstone。详见 [`plan-0.3.x-three-streams/proposal.md`](spec/changes/plan-0.3.x-three-streams/proposal.md)（2026-06-07 重排）。

> **2026-06-07 重排要点**：全端到端自举从 1.0 拉到 0.3.x 作为本线招牌；自举采用「受限写法 + dogfood 补真卡点」（不为自举强制提前整个 0.6/0.7 的 match/LINQ/Result）；REPL 从 0.5.x 拉到 0.3.x capstone。连锁影响见下文 [长期 SemVer 路线](#长期-semver-路线05--10) 重排。

### 0.2.x — 工程化 & 包系统 + perf CI ✅ 收尾

退出标准（已达成）：`.zbc` v1.x / `.zpkg` 格式冻结 ✅；perf CI 上线 ✅；多平台 CI matrix 全绿 ✅；release 自动化产出跨平台 binary ✅。

| 子版本 | 内容 | 估时 |
|------|------|:----:|
| 0.2.0 | `.zbc` v1.x 格式冻结（strict-pin + 6 fixture 字节 golden + workflow.md bump 流程）— [archive/2026-05-14-freeze-zbc-v1](spec/archive/2026-05-14-freeze-zbc-v1/) | 1 周 |
| 0.2.1 | `.zpkg` indexed/packed 格式冻结（strict-pin + 4 fixture 字节 golden + 0.5 → 0.6 catch-up bump）— [archive/2026-05-14-freeze-zpkg-v0](spec/archive/2026-05-14-freeze-zpkg-v0/)；`z42c disasm` 完整化作为另一半（视实施 — follow-up spec）| 1 周 |
| 0.2.2 | Benchmark 套件骨架（`cargo bench` + BenchmarkDotNet）+ 初始基线 | 1.5 周 |
| 0.2.3 | ✅ Perf CI + 性能预算 (`.github/workflows/bench-pr.yml`, 2026-06-05) — PR-side workflow fetches baseline from `bench-baselines` branch, runs `xtask bench --diff --threshold-time 0.10`, fails on >10% time regression | 1 周 |
| 0.2.4 | 🟡 部分 — ✅ `lint-manifest` WS008/WS009 (2026-06-04 `2c5a1881`); ❌ `z42c new/init/fmt/clean` + 独立 `z42-fmt` binary 推 0.4.x | 1 周 |
| 0.2.5 | ✅ 多平台 CI matrix（[ci.yml](../.github/workflows/ci.yml) 5 平台 build/test）+ CI 模板 | 1.5 周 |
| 0.2.6 | ✅ Release 自动化（[release.yml](../.github/workflows/release.yml) tag → 跨平台 binary + zpkg；[archive/2026-05-14-add-release-automation](spec/archive/2026-05-14-add-release-automation/)）| 1 周 |

### 0.3.x — 自举线（GC v1 地基 → stdlib ‖ 全自举 ‖ 反射 → REPL）（2026-06-07 重排）

退出标准：（A）stdlib 重组完成 + 每包 bench baseline + 三轮 perf 攻坚；（B）**编译器 7 子系统全部用 z42 重写**，byte-identical CI gate 7 日零飘移 + end-to-end compile-perf median ≤ 3× C# + z42c-selfhost 下全测试绿；（C）反射 MVP（只读元数据 + `typeof`/`GetType` + Attribute）；（capstone）z42 原生 REPL。

> **完整规划**见 [`plan-0.3.x-three-streams/proposal.md`](spec/changes/plan-0.3.x-three-streams/proposal.md)（2026-06-07 重排，supersede 2026-06-05 保守版）。以下为子版本索引。
>
> **B 主线＝本版本招牌（全自举，从原 1.0 拉到 0.3.x）**：7 子系统 = `z42.{Core,Syntax,Project,Driver,Semantics,IR,Pipeline}` 1:1 镜像 C# 项目，源码落 `src/z42c/` 独立顶级目录（与 `src/compiler/` 平级；2026-06-07 User 裁决，覆盖原 `src/z42.compiler/`；子目录名==包名 `z42c.<sub>`，产物 `z42c.<sub>.zpkg`）。**受限写法**：class+虚方法替代 record+match / 循环替代 LINQ / 异常替代 Result；只有自举真卡点才 dogfood 在 z42 里补该特性（禁止 workaround，per `feedback_dogfood_fill_gaps`）。**无桥接**：z42 端只 ship 就绪命令（0.3.4 起 lex/parse/manifest-check、0.3.9 起 build），0.3.x default 编译器仍是 C#，两实现并存逐字节对账。
>
> **受限写法 ⇒ 不强制提前半个 L3**：match/ADT/LINQ/Result 完整版仍在 0.6/0.7；只有被自举单点阻断的特性才按 features.md 逐项评估提前。这是「受限写法」决策的直接后果。
>
> **REPL = capstone（从原 0.5.x 拉到 0.3.x）**：自举端到端 build 跑通后落地（前置 Semantic/TypeChecker/IR 均在本线内交付），单独 spec `add-z42-repl`。
>
> **C 主线 MVP 不含 Method.Invoke**：完整 Invoke 强依赖 generic instantiation，推 0.5.x L3-R 完整版。
>
> **C3 Attribute reflection 前置**：用户自定义 attribute 机制 spec 需先落地（0.3.4 起草）。

**0.3.0（地基）**：GC v1 —— 抽象 GC 接口 + mark-and-sweep（替换 `Rc<RefCell>`），A/B/C 共同前置（估 2–3 周）。

| 子版本 | B 自举（招牌）| A stdlib | C 反射 |
|:--:|------|------|------|
| 0.3.1 | B0 架构 spec + 建 `src/z42c/` 7 子包骨架 + xtask `build/test compiler-z42`（[scaffold-z42c-selfhost](spec/changes/scaffold-z42c-selfhost/)）| A0 包审计 spec | C0 反射 API spec（新建 `reflection.md`）|
| 0.3.2 | — | A1 包重组（先行，稳定 B 引用路径）| — |
| 0.3.3 | core + syntax（Lexer/Parser/AST）+ bit-identical gate | A2 bench baseline | C1 metadata 暴露 + 4 反射对象 + `GetMembers` 系列 |
| 0.3.4 | project + driver（lex/parse/manifest-check 可跑）| A3 perf #1 BigInt/Coll | C2 `typeof(T)` + `obj.GetType()` + z42.reflection 包公开 |
| 0.3.5 | **semantics**（首个硬子系统，dogfood 高发段）| A4 perf #2 String/IO | C3 Attribute（前置 attribute 机制 spec）|
| 0.3.6 | typecheck | A5 perf #3 JSON/YAML/TOML | — |
| 0.3.7 | ir（codegen + lowering，寄存器 SSA）| | |
| 0.3.8 | emit（ZbcWriter/ZpkgWriter → byte-identical .zbc/.zpkg）| | |
| 0.3.9 | pipeline → **端到端 z42 build 跑通** | | |
| 0.3.10 | byte-identical gate 全 7 子系统绿 + compile-perf gate（median ≤3× / P99 ≤5×）启用 | | |
| 0.3.11 | **REPL capstone**（z42 原生）| | |
| 0.3.12 | 收尾：z42c-selfhost 下全 dotnet/xtask test 绿 + soak + A perf delta report | | |

**‖ = 三主线在该子版本并行推进**。子版本号弹性——本线终点由退出标准定义，自举 dogfood 补特性时插入特性 spec 子版本。

**重排沿革**：
- **2026-06-07（全自举）**：原"B 只做 4 子系统（Lexer/Project/Driver/Parser）+ 剩余推 0.5.x"→ 全 7 子系统并入本线；原"REPL 推 0.5.x"→ 本线 capstone；原"byte-identical 推 1.0"→ 本线退出标准；原"compile-perf gate 0.5.x 启用"→ 0.3.10 启用。
- **2026-06-05（从 0.3.x 移出，仍生效）**：Golden 全 L1 覆盖 + interp/JIT 一致性 CI / 调试符号 / Profiler hooks → 0.4.x 起；热重载 VM 完整实现 → 0.5.x 起；GC v1 → 0.3.0（提前）。

### 0.4.x — 标准库 v1 + test/bench/docgen 工具链

| 子版本 | 内容 | 估时 |
|------|------|:----:|
| 0.4.0 | `z42.core` 完整：Object / Convert / Assert / IEquatable / IComparable / IDisposable | 1.5 周 |
| 0.4.1 | Exception 体系完整 + 9 标准子类 + IEnumerable<T> 完整 | 1 周 |
| 0.4.2 | `z42.io`：文件读写 + stdin/stdout + Path 操作 | 1.5 周 |
| 0.4.3 | `z42.math`：libm 绑定 + 常量 | 1 周 |
| 0.4.4 | `z42.collections`：List/Dict 纯脚本替换 pseudo-class + Queue/Stack | 2 周 |
| 0.4.5 | `z42.text`：字符串操作 + StringBuilder | 1 周 |
| 0.4.6 | **`z42.test` v1 + `z42c test`**：注解发现 + Assert 扩展 + golden 集成 | 2 周 |
| 0.4.7 | **`z42.bench` v1 + `z42c bench`**：warmup + 多次迭代 + JSON + baseline diff | 2 周 |
| 0.4.8 | **`z42-doc` 文档生成器**：doc comment → HTML / markdown + stdlib 自动发布 | 1.5 周 |

---

## 长期 SemVer 路线（0.5 → 1.0）

> 高层 charter；每个 minor 启动时再开 spec 排具体子版本。设计原则：每个 minor 是独立可发布单位（用户可感知能力跃迁）；每个 patch 是独立 spec。

| 版本 | 主题 | Phase | 估时 |
|------|------|:----:|:----:|
| **0.5.x** | 泛型完整 + Trait 静态分发 + 反射**完整版**（Method.Invoke，依赖 generic instantiation）+ LSP v1 + Interop 2a（Rust embedding 稳定）| L3 | 10–14 周 |
| **0.6.x** | 函数式（Lambda / 命名参数 / 模式匹配 / `let` 不可变 / LINQ）+ unmanaged + GC v2 + linter | L3 | 9–11 周 |
| **0.7.x** | `Result<T,E>` + `?` + ADT + `match` 穷尽检查 | L3 | 6–8 周 |
| **0.8.x** | async / await + 多线程 + GC v3（generational + concurrent）+ DAP debugger | L3 | 12–16 周 |
| **0.9.x** | 单文件脚本 + 嵌入 API GA + 可裁剪 + WASM target + Interop 2b（manifest reader / source generator）| L3 | 10–14 周 |
| **0.10.x** | 性能强化（philosophy §9 五指标全部达标）| L3 | 8–12 周 |
| **1.0.x** | 删 C# bootstrap（自举核心已在 **0.3.x** 完成 byte-identical）+ 跨架构 NativeAOT + Interop 3 + `z42up` 工具链 GA + SemVer / deprecation 启用 | L3+ | 8–12 周 |

**累计估算**：~16–20 个月（按全职 1 人节奏）。

### 跨版本关键依赖

```
0.1 ─► 0.2 ─► 0.3 ──┬──► 0.4 ──► 0.5 ──► 0.6 ──► 0.7 ──► 0.8 ──► 0.9 ──► 0.10 ──► 1.0
       │       │   │           │                                            │
       │       │   ├── reflection MVP (0.3 C) ──► reflection 完整 (0.5.1–0.5.3 + test 增强 0.5.6)
       │       │   │                                                        │
       │       │   ├── 编译器全自举 7 子系统 (0.3 B：Lex→Parse→Proj→Driver→Sem→TC→IR→Emit→Pipeline)
       │       │   │           ──► byte-identical gate + compile-perf ≤3× (0.3.x 退出)
       │       │   │                          ──► 删 C# bootstrap (1.0 收尾)
       │       │   │                                                        │
       │       │   ├── GC v1 (0.3.0，从 0.3.3 提前) ──► GC v2 (0.6) ──► GC v3 (0.8) ─►
       │       │   │                                                        │
       │       │   └── stdlib 重组 + perf (0.3 A) ──► stdlib v1 (0.4)
       │       │                                                            │
       │       └─── benchmark 套件 ─► perf CI (0.2.3) ──持续生效──────────► │
       │                                                                    │
       └─── .zbc/.zpkg 格式冻结 ─────► 1.0 SemVer 启用 ────────────────────► │
```

强依赖链：
- 0.3 A perf 攻坚 ◄── 0.3.0 GC v1（无稳定 GC 的 micro-opt 无意义）
- 0.3 B 编译器全自举 ◄── 0.3.0 GC v1（z42 端编译器对 GC 压力大）
- 0.3 B 自举受限写法 ◄── 泛型 G1-G4 + 闭包核心（已提前落地）；缺 match/LINQ/Result 用 class+虚方法 / 循环 / 异常替代，真卡点才 dogfood 提前
- 0.3 C3 Attribute reflection ◄── 用户自定义 attribute 机制（features.md §X，0.3.5 前先 spec）
- 0.5 反射完整版 Method.Invoke ◄── 0.5 L3-G 泛型 instantiation
- 0.5 反射 ◄── 0.10 性能数据自查（type metadata access）
- 0.6 unmanaged ◄── 0.9.6 C ABI 头文件
- 0.7 Result ◄── 0.8 async（async fn 通常返回 `Task<Result<T,E>>`）
- 0.8 GC v3 ◄── 0.9.5 VM 组件化
- 0.10 性能基线 ◄── 1.0 稳定承诺
- 1.0 删 C# bootstrap ◄── 0.3.x 自举 byte-identical gate 跑稳（自举核心不再等全部 L3；受限写法已规避 match/LINQ/Result）

---

## Feature → Version 映射

每个 features.md 章节落地到哪个 minor。

| features.md 章节 | 所属 minor | 当前状态 |
|------|:------:|:----:|
| §1 Type System / §2 Null Safety / §3 Memory Management / §4 Error Handling (exceptions) / §5 Type Definitions (class/struct/record) / §6 Functions / §7 Control Flow / §8 Strings / §9 Collections / §10 Imports / §11 Numeric Aliases | 0.1.x | ✅ L1 |
| §12 Hot Reload | 0.5.x（从 0.3.2 推后；GC v1 后真热更新落地）| 🟡 设计有 |
| §13 Execution Mode Annotations | 0.1.x（注解）→ 0.5.x（运行时切换；从 0.3.x 推后）| 🟡 注解 ✅；运行时切换待 |
| §14 Generics + Trait | 0.5.x | ✅ G1-G4 + L3-Impl 提前落地 |
| §15 Reflection | **MVP 0.3.x（只读元数据 + typeof + Attribute）/ 完整 0.5.1–0.5.3（含 Method.Invoke）** | 🟡 C1 GetType 路线落地（[archive/2026-06-09-add-reflection-mvp](spec/archive/2026-06-09-add-reflection-mvp/)：Type + Std.Reflection.{Field,Method,Parameter}Info + GetType 句柄化）；**C2 typeof→Type 落地**（[archive/2026-06-09-make-typeof-return-type](spec/archive/2026-06-09-make-typeof-return-type/)：`typeof(T)` 返回 Std.Type，用户类带真句柄）；**C3 Attribute 落地（class + method）**（[archive/2026-06-09-add-attribute-reflection](spec/archive/2026-06-09-add-attribute-reflection/) class-level + [archive/2026-06-09-add-attribute-reflection-methods](spec/archive/2026-06-09-add-attribute-reflection-methods/) method-level：`class Foo : Attribute` + `[Foo]` on class/method → `Type`/`MethodInfo` GetCustomAttributes 活实例；factory-thunk；改进 C# 5 处缺陷；见 [attributes.md](design/language/attributes.md)）；L3-R Invoke 待 |
| §16 Lambda + Closure | 0.6.0 | ✅ L2-C1 + L3-C2 核心提前落地 |
| §17 Result + ADT + match | 0.7.x | 📋 |
| §18 可裁剪 / Tree-shaking / 200KB 子集 | 0.9.x（嵌入 / 裁剪）+ 1.0-rc（AOT 静态链接）| 📋 |
| §19 NativeAOT | 1.0.x | 📋 |
| §20 Interop 三层 ABI | 0.5.5 / 0.9.x / 1.0.x | ✅ Tier 1 + Tier 2 + manifest 提前落地 |

> "提前落地" = L2 阶段已实施部分 L3 特性，未对应到 0.x.0 minor 但代码已在 main。

---

## 横向工作流（贯穿所有版本）

| 工作流 | 启用版本 | 内容 |
|------|:------:|------|
| Benchmark 套件 | 0.2.2 | `cargo bench` + BenchmarkDotNet 骨架 |
| Perf CI | 0.2.3 | 关键 benchmark > 10% 退化阻塞 commit |
| 多平台 CI matrix | 0.2.5 | macOS / Linux / Windows × x86_64/arm64 全绿 |
| 项目级 CI 模板 | 0.2.5 | `z42c new` 自带 GitHub Actions / GitLab CI 模板 |
| Release 自动化 | 0.2.6 | git tag → 跨平台 binary + zpkg 自动产出 |
| 跨平台 SDK package 分发 | 0.2.6 | 13 个 per-arch SDK 包（desktop × 5 / iOS × 3 / Android × 4 / wasm × 1）；统一 `bin/libs/native/examples/manifest.toml` 形态；详见 [embedding.md §11.9](design/runtime/embedding.md#119-分发-package-形态per-arch-flat2026-05-13-define-package-layout) |
| 跨 mode 一致性 CI | 0.3.0 | interp / JIT 同测试集结果一致 |
| `z42c test` GREEN 门禁 | 0.4.6 | stdlib + 用户代码 z42 测试纳入 GREEN |
| `z42c bench --diff` | 0.4.7 | z42 代码 bench 进 perf CI |
| `z42-doc` 自动发布 | 0.4.8 | 标准库 doc comment → 静态站点 |
| LSP 集成测试 | 0.5.7 | LSP server 协议级 conformance test |
| DAP debugger conformance | 0.8.7 | VS Code / JetBrains 调试 |
| WASM target CI | 0.9.7 | VM 编译为 WASM + headless 浏览器跑 |
| 跨 mode bench 对比 | 0.10.x | interp / JIT / AOT 三模 bench 报告 |
| 跨架构 perf 矩阵 | 1.0-rc1 | x86_64 / arm64 / wasm32 perf 进 release notes |

### 多平台支持矩阵

| 平台 | 编译器 | VM | NativeAOT | 起始版本 |
|------|:---:|:---:|:---:|:----:|
| macOS x86_64 / arm64 | ✅ | ✅ | ✅ | 0.2.5 |
| Linux x86_64 / arm64 | ✅ | ✅ | ✅ | 0.2.5 |
| Windows x86_64 | ✅ | ✅ | ✅ | 0.2.5 |
| Windows arm64 | ✅ | ✅ | ⚠️ rc | 1.0-rc2 |
| WASM (wasm32-wasi) | — | ✅ VM only | — | 0.9.7 |
| iOS / Android | — | 🔬 实验 | 🔬 实验 | 1.x+ |
| 嵌入式（no_std）| — | 🔬 实验 | — | 1.x+ |

### Toolchain 矩阵

| 工具 | 用途 | 起始版本 | GA 版本 |
|------|----|:----:|:----:|
| `z42c` | 编译器驱动（build/check/run/test/bench/fmt/clean/disasm/explain/new/init/doc）| 当前 | 0.4.x |
| `z42vm` | VM 运行时 | 当前 | 0.9.x |
| `z42-fmt` | 代码格式化 | 当前 | 0.2.4 |
| `z42-doc` | API 文档生成 | 0.4.8 | 0.4.8 |
| `z42-lsp` | Language Server Protocol | 0.5.7 | 0.6.7 |
| `z42-lint` | 静态检查 | 0.6.7 | 0.7.x |
| `z42-dap` | Debug Adapter Protocol | 0.8.7 | 0.9.x |
| `z42up` | 版本管理工具 | 1.0-rc6 | 1.0 |
| `z42-pkg` | 包注册表客户端 | 1.x+ | 1.x+ |

### GREEN 标准演进（任一时点 = 该时点之前所有项的累积）

| 起始版本 | 新增 GREEN 项 |
|:------:|------|
| 当前 | `dotnet build` + `cargo build` + `dotnet test` + `z42 xtask.zpkg test vm` 全绿 |
| 0.2.3 | Perf CI |
| 0.2.5 | 多平台 CI matrix |
| 0.3.10 | z42c-selfhost byte-identical gate（7 子系统逐字节对账）+ compile-perf ≤3× C# |
| 0.4.6 | `z42c test` 100% 通过 |
| 0.4.7 | `z42c bench --diff` 通过 |
| 0.4.8 | `z42-doc` 无错 |
| 0.5.0 | 跨 zpkg 反射元数据一致性 |
| 0.5.7 | LSP conformance |
| 0.6.7 | `z42-lint` 零警告 |
| 0.8.6 | 多线程压力测试（race detector）|
| 0.8.7 | DAP conformance |
| 0.9.7 | WASM target build & test |
| 0.10.0 | philosophy §9 五指标自动化基线 |
| 1.0.0 | C# bootstrap 删除后 z42c-selfhost 唯一编译器全绿 + 跨架构 perf 数字 |

---

## 实现里程碑（pipeline 维度）

| 里程碑 | 内容 | 所属阶段 | 状态 |
|--------|------|:-------:|:----:|
| M1 | Lexer + Parser | L1 | ✅ |
| M2 | TypeChecker（L1 特性全覆盖）| L1 → L2 | ✅ |
| M3 | IR Codegen → `.zbc`（L1 特性全覆盖）| L1 → L2 | ✅ |
| M4 | VM Interpreter（L1 特性全覆盖）| L1 | ✅ |
| M5 | VM JIT（Cranelift，L1 特性）| L1 → L2 | ✅ |
| M6 | 工程支持 + 测试体系 + `.zbc` 格式稳定 | L2 | ✅ |
| M7 | VM 元数据 + 标准库基础（core/io/collections）| L2 | 🟡 stdlib 基础已广；反射元数据 → 0.3.x C 主线 |
| M8 | TypeChecker + Codegen 扩展（L3 特性）| L3 | 🟡 部分（泛型 / lambda / delegate 提前）|
| M9 | VM AOT（LLVM/inkwell）| L3 | 📋 |
| M10 | 自举（Self-hosting，7 子系统 byte-identical）| L3+ → 0.3.x | 🚧 进行中（B0 骨架 + 构建管线落地 2026-06-07 [scaffold-z42c-selfhost](spec/changes/scaffold-z42c-selfhost/)；架构见 [self-hosting.md](design/compiler/self-hosting.md)；core/syntax 等后续）|

---

## 待裁决问题（Q1–Q18）

> 以下问题在对应版本启动 spec 时由 User 裁决；提前列出避免实施时阻塞。

| # | 版本 | 问题 |
|:--:|:----:|-----|
| Q1 | 0.3.3 | GC v1 放 0.3 还是延后到 0.8 与多线程一起？（暂定方案：0.3）|
| Q2 | 0.4.6 | `z42.test` 注解风格：`[Test]`（C#）vs `test "name" {}`（Zig）？（推荐 C# 风）|
| Q3 | 0.5.4 | Trait 与 interface 是否完全等价？同一类型可同时实现两者？|
| Q4 | 0.6.0 | 闭包变量捕获：值捕获 vs 引用捕获 vs 显式标注？|
| Q5 | 0.6.3 | 引入 `let` 后是否提供 `var → let` codemod？|
| Q6 | 0.7.1 | `Option<T>` 与 `T?` 是否可隐式互转？编译器层视为同一类型？|
| Q7 | 0.8.5 | 数据竞争预防：Send/Sync trait 还是注解 + 编译器分析？|
| Q8 | 0.9.5 | VM 组件化粒度：cargo feature 还是构建时 build profile？|
| Q9 | 0.10.x | 性能强化 9 个 patch 独立发布还是合并 0.10.0 单次？|
| Q10 | 1.0 | AOT 是否必须卡 1.0？（备选：1.0 = 自举 + 稳定，1.1 = AOT）|
| Q11 | 0.2.5 | 多平台 CI 选 GitHub Actions matrix 还是自托管 runner？arm64 主机如何获取？|
| ~~Q12~~ | ~~0.2.5~~ | ~~Release artifact 命名~~ — 已裁决 2026-05-14：`z42-<version>-<rid>.{tar.gz\|zip}`（9 RID；windows-x64 用 zip，其余 tar.gz；含 SHA256SUMS）。详见 [archive/2026-05-14-add-release-automation/design.md](../docs/spec/archive/2026-05-14-add-release-automation/design.md)。|
| Q13 | 0.5.7 | LSP server 用 .NET（复用编译器）还是 Rust（复用 VM）？|
| Q14 | 0.8.7 | DAP debugger 多线程暂停语义：单 thread 还是全部？JIT/AOT 如何 step？|
| Q15 | 0.9.7 | WASM 下 GC：等 wasm-gc proposal 还是自实现 wasm-internal GC？|
| Q16 | 0.9.8 | 嵌入式 ~200KB 平台基准（cortex-M4 / esp32 / RISC-V？）|
| Q17 | 1.0-rc6 | `z42up` 用 Rust 还是等自举后用 z42 自身实现？|
| Q18 | 1.x+ | 包注册表中心化（crates.io 模式）还是去中心化（go modules / git URL）？|

---

## Deferred Backlog Index

> 所有显式延后特性的横向索引；条目正文存于对应 design doc 的 "Deferred / Future Work" 段。新增延后项时：① 在对应 design doc 加条目 ② 在本表加索引行。规则见 [`.claude/rules/philosophy.md`](../.claude/rules/philosophy.md#延后特性管理必须遵守) "延后特性管理"。

### 设计期延后

| 特性 | 描述 | 在哪里 |
|------|------|------|
| L3-G3a 关联类型 | `where T: IAdd<Output=T>` + zbc 扩展 + 运行时校验 | [language/generics.md](design/language/generics.md) |
| 闭包档 A 完整版 | 任何不逃逸 closure 栈分配（当前仅单变量子集）| [language/closure.md](design/language/closure.md) |
| 闭包档 B 完整版 | 单态化 + 泛型形参标注（当前仅 alias 子集）| [language/closure.md](design/language/closure.md) |
| 闭包档 C send 派生 | 与 concurrency 实施一起做 | [language/closure.md](design/language/closure.md) |
| Static abstract iter 2+ | 类型级访问（`T.Zero` / `T.Parse(s)`）| [language/static-abstract-interface.md](design/language/static-abstract-interface.md) |
| ref local / return / field / struct | parameter-modifiers D1-D4 | [language/parameter-modifiers.md](design/language/parameter-modifiers.md) |
| StackTrace / 构造器重载 / 字段 ? 标注 / self-assign | exceptions Phase 1 限制 | [language/exceptions.md](design/language/exceptions.md) |
| Layer 3 用户定义 operator/keyword | customization 第三层 | [language/customization.md](design/language/customization.md) |
| foreach IEnumerator 路径 | 升级为接口 dispatch（当前仅鸭子协议）| [language/iteration.md](design/language/iteration.md) |
| 自定义 body / init-only / expression-bodied property | properties 未支持子集 | [language/properties.md](design/language/properties.md) |
| `Type : MemberInfo` 层级对齐 | 统一 Type 不拆 TypeInfo（2026-06-09 已定）；但 Type 当前非 MemberInfo 子类、不在 Std.Reflection——对齐留待嵌套类型反射 / 自举镜像时 | [language/reflection.md](design/language/reflection.md#deferred--future-work) |
| 继承的静态字段反射 | `GetFields()` 含静态已落地（2026-06-10）但仅声明类自身；继承静态需沿 base 链聚合 `static_fields` | [language/reflection.md](design/language/reflection.md#deferred--future-work) |
| Tier 2/3 完整 interop | manifest reader / 源生成 / symbol resolution | [language/interop.md](design/language/interop.md) |
| 整体 L3 concurrency | async/await / Future / Send-Sync / 调度器 | [runtime/concurrency.md](design/runtime/concurrency.md) |
| hot-reload 签名变更 + 跨模块 | 签名变更检测 / 跨模块 reload 故事 | [runtime/hot-reload.md](design/runtime/hot-reload.md) |
| 完整 JIT 指令映射 + 性能基准 | jit.md 待补 | [runtime/jit.md](design/runtime/jit.md) |
| GC handle Phase 3+ | Pinned / WeakTrackResurrection / 多线程 barrier | [runtime/gc-handle.md](design/runtime/gc-handle.md) |
| launcher 下载/install/self-update (P2) | `z42 install/uninstall/self update` + 每平台×版本发布点 + 校验（P1 用 `z42 link` 本地注册） | [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| launcher app 版本声明格式 | zpkg `META.toolchain_version` vs `runtimeconfig.json` sidecar 未定；分发(P2)时才需 | [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| z42c 裸脚本→Exe-zpkg | 原 launcher phase 0.5；现以 mini-project(`kind="exe"` toml) workaround，ROI 低 | [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| apphost self-contained | `--self-contained`：VM+libs 随 app 本地化（P1 仅 framework-dependent）| [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| apphost single-file | 链 `libz42_vm` + 内嵌 zpkg/libs，经 embedding C ABI 内存加载；依赖 C ABI + 碰 runtime | [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| apphost Windows checksum/Authenticode + 跨平台交叉签名 | Windows PE checksum / 在 Linux 上签 macOS apphost（需内建 Mach-O 签名器；P1 用 host codesign）| [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| apphost cwd 上行 / 富搜索配置 | P1 本地搜索仅 exe 目录上行 | [runtime/launcher.md](design/runtime/launcher.md#deferred--future-work) |
| stdlib 剩余缺失包 | **async** 仍延后（依赖 L3 async/await 语法）；~~fs~~ ✅ / ~~os~~ ✅（合入 z42.io）/ ~~threading~~ ✅ 2026-05-20 / ~~net~~ ✅ K1-K4 2026-05-24~05-25 / ~~crypto~~ ✅ SHA-1/256+HMAC 2026-05-24~05-25。详 `docs/design/stdlib/roadmap.md` | [stdlib/roadmap.md](design/stdlib/roadmap.md) |
| split-debug-symbols 退化 trace ip+build_id | line==0 时帧追加 `+0x<ip> [build:<8hex>]`；需 VmFrame 追踪 PC | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| `z42c symbolicate` 离线工具 | 把 `.zsym` 应用到 crash trace 还原 file:line:col | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| sidecar lazy / mmap 加载 | 启动延迟敏感场景的优化路径 | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| sidecar 跨目录搜索 | debuginfod 风格 + 环境变量配置 | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| `Std.Reflection.Symbolicate` 公开 API | 程序内触发符号化 | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| Facade threading 测试（R8）| 等 runtime threading 模型落地后回到 platform-test-contract 补"后台 invoke + 主线程 sink"scenario | [runtime/embedding.md §12](design/runtime/embedding.md#§12-deferred明确不做的) |
| multi-arch-container-packages | multi-slice xcframework / multi-ABI AAR 卷起来发；Phase 1 选 per-arch flat（13 包），用户呼声出来再加 `z42-<v>-ios-xcframework-<config>` / `z42-<v>-android-aar-<config>` 两个 convenience 包 | [runtime/embedding.md §11.9](design/runtime/embedding.md#119-分发-package-形态per-arch-flat2026-05-13-define-package-layout) |
| per-arch-abi-feature-matrix | abi-version 升 2 后"哪些 host config 字段哪个 ABI 起可用"细粒度矩阵 | [runtime/embedding.md §11.9](design/runtime/embedding.md#119-分发-package-形态per-arch-flat2026-05-13-define-package-layout) |
| binary-package-signing | iOS xcframework / Android AAR / wasm npm publish 时 notarization / GPG / npm 2FA；Phase 1 全 unsigned，留给 Phase 4 release CI | [runtime/embedding.md §11.9](design/runtime/embedding.md#119-分发-package-形态per-arch-flat2026-05-13-define-package-layout) |
| z42 build-driver prerequisites | 用 z42 自身重写所有 `.sh` 解 Tier 1 Windows CI；阻塞 = P0 z42.os/z42.io.fs + P1 z42.crypto/z42.net + P2 z42.toml/z42.compression | [stdlib/roadmap.md "Deferred / Future Work"](design/stdlib/roadmap.md#z42-build-driver-prerequisites2026-05-13) |
| ~~pre-existing cargo test build break~~ ✅ | 已修复 2026-05-27 `f7c15058` —— 根因是 `gc::region_tests` / `arc_heap_tests::invariants` 调 `#[cfg(debug_assertions)]` 方法但模块仅 `#[cfg(test)]`。2 行 fix 把模块 cfg 收紧到 `cfg(all(test, debug_assertions))`。验证：release 673/673 + debug 716/716 全绿 | — |
| ~~URL-safe Base64~~ ✅ + ~~Base32~~ ✅ + ~~UTF-16/32~~ ✅ + ~~Crockford~~ ✅ + ~~Base32-hex~~ ✅ + Encoding streaming / Base85 | **Base64Url / Base32 已落地 2026-05-25**；**UTF-16 + UTF-32 已落地 2026-05-27** (`37b7191e`)；**Base32Crockford + Base32Hex 已落地 2026-05-25**；仅 **Encoding streaming API + Base85** 仍延后 | [stdlib/encoding.md](design/stdlib/encoding.md#deferred--future-work) |
| HMAC-SHA256 | v0 SHA-256 落地后的下一步；RFC 2104 公式 | [stdlib/crypto.md](design/stdlib/crypto.md#hmac-sha256) |
| ~~Std.Crypto.SecureRandom (CSPRNG)~~ ✅ | **✅ 已落地 2026-05-26** (add-csprng-to-crypto)；wasm32 bridge 仍延后 | [stdlib/crypto.md](design/stdlib/crypto.md#csprng-wasm32-bridgestdcryptosecurerandom-on-wasm32) |
| ~~Zip.Write~~ ✅ + Zip.CreateFromDirectory | **Zip.Write 已落地 2026-05-27** (`add-zip-write`，single-buffer 2-pass 绕过 byte[][])；仅 **`Zip.CreateFromDirectory`**（atop Zip.Write + Directory.Enumerate）仍延后 | [stdlib/compression.md](design/stdlib/compression.md#compression-future-zip-create-from-directory) |
| ~~Compression streaming decode~~ ✅ | **cdylib 流式 2026-05-27** (`add-compression-streaming-decode`) + **z42 消费端 per-chunk pull 2026-06-09** (`compression-decoder-pull-mode`) → 流式解压端到端，不再 accumulate-then-decompress | [stdlib/compression.md](design/stdlib/compression.md#compression-future-streaming-decode) |
| Brotli / xz / LZ4 | z42.compression v0 算法之外 | [stdlib/compression.md](design/stdlib/compression.md#compression-future-brotli) |
| wasm zstd | 需 WASI SDK 或 ruzstd | [stdlib/compression.md](design/stdlib/compression.md#compression-future-wasm-zstd) |
| YAML ~~anchors~~ ✅ / ~~tags~~ ✅ / ~~multi-line~~ ✅ / ~~multi-doc~~ ✅ / ~~timestamps~~ ✅ / ~~hex-octal~~ ✅ / ~~merge-keys~~ ✅ / complex-keys | **anchors / tags / multi-line / multi-doc / timestamps / numeric-bases / merge-keys 全部已落地** (2026-05-25 → 2026-06-01)；仅 `yaml-future-complex-keys` (`? key` 语法) 仍延后 — rare in practice | [stdlib/yaml.md](design/stdlib/yaml.md#deferred--future-work) |
| ~~FileStream~~ ✅ + ~~TextReader~~ ✅ + ~~BufferedStream~~ ✅ + async streams | **`FileStream` 已落地 2026-05-24**；**TextReader/TextWriter 已落地 2026-05-28** (`e80f0311`)；**BufferedStream 已落地 2026-05-24**；仅 **async streams**（需 L3 async）仍延后 | [stdlib/io-stream.md](design/stdlib/io-stream.md#deferred--future-work) |
| ~~Refactor CompressionStream to Stream~~ | **✅ 已落地 2026-05-24** — CompressionStream → `WrapWrite/WrapRead` 返回 `Std.IO.Stream` | [stdlib/io-stream.md](design/stdlib/io-stream.md#refactor-compression-stream-on-iostream--landed-2026-05-24) |
| ~~Refactor BinaryReader/Writer to accept Stream~~ | **✅ 已落地 2026-05-24** — `(Stream)` 构造器；byte[] 构造保留作 sugar | [stdlib/io-stream.md](design/stdlib/io-stream.md#refactor-binary-reader-stream--landed-2026-05-24) |
| libdeflate batch | 1.5× DEFLATE 快通道；bench 驱动 | [stdlib/compression.md](design/stdlib/compression.md#compression-future-libdeflate-batch) |
| Migrate existing stdlib natives to ext loader | crypto / 等可选移出 z42vm | [runtime/native-ext-loader.md](design/runtime/native-ext-loader.md#migration-of-existing-stdlib-natives) |
| ~~reader-writer-asymmetry (zbc+zpkg)~~ | ✅ 已修复 by [align-zbc-reader-writer-asymmetry](spec/archive/2026-05-27-align-zbc-reader-writer-asymmetry/) (zbc 1.7 / zpkg 0.8, 2026-05-27)；SIGS / TYPE 在 u8 TypeTag 之后加 u32 type_str_idx 作权威类型名；ReadWriteRoundTrip CI 启用 | — |
| ~~跨包 static field 初始化时机~~ | ✅ 已修复 by `dfcd1495 fix(compiler+vm): unique __static_init__ name per source file`（2026-05-15）；stdlib workaround 由 `cleanup-static-field-workarounds` spec 回收 | — |
| ~~`jit-future-safepoint-inline`~~ | ✅ landed 2026-06-03 as [inline-jit-safepoint-check](spec/archive/2026-06-03-inline-jit-safepoint-check/tasks.md) — `atomic_rmw sub + brif` 内联在 translate.rs 5 处 emit site，slow path 走 `jit_check_safepoint_slow` 新 helper | [archive/2026-05-28-jit-type-specialization/tasks.md](spec/archive/2026-05-28-jit-type-specialization/tasks.md#out-of-scope-items-deferred-for-future-spec) |
| `jit-future-f64-specialization` | F64 `fadd` / `fsub` / `fcmp` 走 native（结构与 I64 完全对称，只是 payload 类型）；等 F64-heavy benchmark 出现再做 | [archive/2026-05-28-jit-type-specialization/tasks.md](spec/archive/2026-05-28-jit-type-specialization/tasks.md#out-of-scope-items-deferred-for-future-spec) |
| TLS 后续（streaming / system-roots / keepalive-pool / server）| `add-z42-net-tls` (2026-06-03) 客户端落地后的 4 项：https `SendStreaming`、honour 系统 CA、TLS 连接池、服务端 TLS | [stdlib/net.md](design/stdlib/net.md#net-future-tls--已落地-2026-06-03-add-z42-net-tls) |

### 实施期延后（D-* 系列）

| ID | 标题 | Design doc 条目 |
|------|------|------|
| **D-2** | ISubscription chain `.AsOnce()` / `.AsWeak()` 跨 generic interface impl | [language/delegates-events.md](design/language/delegates-events.md#d-2-isubscription-chain-asonce--asweak-跨-generic-interface-impl) |
| **D-3** | N>4 arity Action / Func（自举后用 z42 写生成器）| [language/delegates-events.md](design/language/delegates-events.md#d-3-n4-arity-action--func) |
| **D-4** | 协变 / 逆变（`<in T, out R>` 等）| [language/generics.md](design/language/generics.md#d-4-协变--逆变in-t-out-r-等) |
| **D-11** | introduce-bound-visitor（review.md §2.1 visitor 抽象基类）| [compiler/compiler-architecture.md](design/compiler/compiler-architecture.md#d-11-introduce-bound-visitorreviewmd-21-visitor-抽象基类) |
| `compiler-future-typed-overload-resolution` | mangling 改为类型编码键 + IR/zpkg/resolver 同步 + 类型 best-match 选择；解锁 stdlib 类的 `(byte[])` / `(Stream)` 等同元不同类型 ctor / method 重载（当前用 static factory workaround） | [compiler/compiler-architecture.md](design/compiler/compiler-architecture.md#compiler-future-typed-overload-resolution) |
| ~~`compiler-future-vcall-base-class-fallback`~~ ✅ | 已修复 (2026-05-26) — 三处协同修复：IrGen.Classes.cs `.base` 元数据用 QualifyClassName；FunctionEmitter.cs base ctor IR 名从 SemanticModel 推导；exec_vcall.rs lazy walk 对深层 base 用 ctx.try_lookup_type() | [compiler/compiler-architecture.md](design/compiler/compiler-architecture.md#compiler-future-vcall-base-class-fallback-已修复-2026-05-26) |
| `slim-terminator-future` | 装箱 `Terminator` 的 String label（per-block，非热数组，收益低于 Instruction）| [runtime/ir.md](design/runtime/ir.md#deferred--future-work) |
| `slim-instruction-stringid` (E2.P3) | `String → StringId` intern 收敛，进一步缩小 payload struct（正交 slim-instruction-enum 之后）| [runtime/ir.md](design/runtime/ir.md#deferred--future-work) |

### 代码内临时绕过（in-code stopgaps，待正解）

> 2026-06-01/02 修 CI 时落地的过渡手段 —— **代码里有临时绕过，正解在对应 active spec 里排期**。这两项不是 design-doc 延后，而是 `docs/spec/changes/` 下的进行中变更；列在此处供集中复查。

| 绕过点（代码） | 正解 spec | 状态 |
|------|------|------|
| `src/runtime/tests/cross_thread_smoke.rs::concurrent_gc_mode_stress_no_race_no_leak` 在 **windows `#[ignore]`**（并发 GC stale-mark race；windows-only、本地不可复现）| [spec/changes/investigate-concurrent-gc-stale-mark-race](spec/changes/investigate-concurrent-gc-stale-mark-race/) 阶段 3：loom/shuttle 验证 + 协议修复 | ⏳ 待排期 |
| ~~`src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42` 的 `[Timeout]` 600s stopgap~~ | ✅ 已修复 2026-06-05 by [spec/archive/2026-06-05-optimize-ecdsa-jacobian-coords](spec/archive/2026-06-05-optimize-ecdsa-jacobian-coords/)：secp256k1 + P-256 都迁到 Jacobian 坐标（一次 ModInverse / scalar mult），round-trip 本地 ~60s → ~5.5s。`[Timeout]` 收紧到 60s | ✅ 完成 |

### 仓库结构 / 维护方向（infra，未排期）

> 战略展望（非 feature，无 design doc 条目，按 philosophy.md 归 roadmap）。来源：User 2026-06-15「这个仓库只做测试流程的」。

| 方向 | 描述 | 触发条件 |
|------|------|---------|
| `infra-slim-git-history` | **真正的克隆成本在历史**：`.git` ≈ 604 MB 而 HEAD 跟踪内容仅 ~25 MB → 历史含曾提交又删的大二进制（旧 zpkg/artifacts blob）。用 `git filter-repo` 清历史大 blob（预计降到几十 MB）+ 收紧 `.gitignore`（已发现 `src/toolchain/host/examples/**/target/` cargo 构建目录、`examples/*.zbc/.zlib/.zmod` 等混入树）。**与拆库正交，收益最大。** | clone 成本成痛点时 |
| `infra-extract-user-docs` | 本仓收敛为「核心（编译器/VM）+ 测试流程」仓；**仅外迁纯用户面 docs**（语言教程/指南/官网内容）到独立 `z42-docs`/官网仓。**留仓不外迁**（它们是开发/测试流程本体）：`examples/`（216 KB，被 C# 测试 + 打包 + zbc_compat 载重消费）、`docs/spec/`（spec-first 工作流本体）、`docs/design/`（@-included 进 CLAUDE.md）、`docs/workflow/`（build/test 命令真相源）。注意：拆当前文件到新仓**不会**缩小本仓 `.git`，须配合 `infra-slim-git-history`。 | 用户面文档成规模时 |

### 平台测试 CI / 后续（add-platform-test-pipeline 之后）

> 三平台 xtask 三阶段框架已落地（2026-06-16，wasm 端到端验证 7/7）。剩余：

| 方向 | 描述 | 触发 |
|------|------|------|
| `infra-ci-platform-test-dashboard` | CI job 跑 wasm(ubuntu+Playwright) / iOS(macos runner + Simulator `xcodebuild test`) / Android(`reactivecircus/android-emulator-runner` + KVM) 三平台 `test platform`，各产 JUnit → **GitHub Checks**（test-reporter action）聚合成 PR check runs = 跨平台测试 dashboard。GitHub 即远程同步层，无需自建服务 | 下一步（User 2026-06-16 要求）|
| `port-android-emulator-run-to-z42` | AndroidBackend.RunTests 当前桥接 `test.sh`（emulator AVD boot/poll/kill）；完整 z42 化 + JUnit 转换 | CI 稳定后 |
| `ios-simulator-test` | IosBackend.RunTests 当前 `swift test`（macOS host slice）；加 iOS Simulator `xcodebuild test -destination` 执行 + JUnit | CI 接入时 |
| `retire-platform-build-test-sh` | 三平台 z42 管线 CI-proven 后，删 `platforms/*/{build,test}.sh`（migrate-scripts-to-z42 节奏）| CI-proven 后 |

### Backlog 项实施流程

每条 deferred 项被实施时：
1. 把对应条目从 design doc Deferred 段移入实施 spec 的"实施备注"
2. 创建 `<spec-name>` 类型的独立 spec
3. 验证 + GREEN 后归档；design doc Deferred 段移除该条目，本表索引行同步删除

---

## 已完成的实施

> 不在本文复述。每个落地特性都在 [`docs/spec/archive/YYYY-MM-DD-<spec-name>/`](spec/archive/) 下保留完整 proposal / design / specs / tasks / 实施备注。按主题或日期检索即可：
>
> - **L1 全特性**：`2026-04-04-*` 至 `2026-05-05-*`（pipeline / 工程文件 / 异常 / interface / inheritance / 参数修饰符）
> - **L2 工程支持**：`2026-04-26-*` workspace 系列、`2026-04-27-incremental-build-cache`
> - **L2 测试体系（R 系列）**：`2026-04-29-redesign-test-infra` / `2026-04-30-add-z42-test-runner` / `2026-05-05-extend-z42-test-library`
> - **L2 GC（MagrGC）**：`2026-04-29-add-magrgc-*` 系列（heap-interface / cycle-breaking-collector / drop-time-finalizer / strict-oom-rejection / external-root-scanning）
> - **L2 Interop**：`2026-04-29-impl-tier1-c-abi` / `2026-04-29-impl-tier2-rust-macros` / `2026-04-29-impl-pinned-syntax` / `2026-04-30-manifest-reader-import` / `2026-04-30-synthesize-native-class`
> - **L2 Embedding**：`2026-05-10-add-embedding-api`（H0-H3） / `2026-05-12-add-zpkg-resolver-hook`（H4 前置；platform facade 注入 zpkg 字节的 hook） / `2026-05-12-add-platform-wasm`（H4 WASM facade）/ `2026-05-12-add-platform-ios`（H4 iOS facade —— `Z42VM.xcframework` SwiftPM 包）/ `2026-05-12-add-platform-android`（H4 Android facade —— `z42vm.aar` Gradle module）
> - **L3 泛型 G1-G4**：`2026-04-22-add-generics-*` / `2026-04-23-add-generics-*` / `2026-04-24-add-static-abstract-interface`
> - **L3 闭包 / Lambda**：`2026-05-01-impl-lambda-l2` / `2026-05-01-impl-closure-l3-core` / `2026-05-02-impl-closure-l3-jit-complete`
> - **L3 Delegate / Event**：`2026-05-02-add-delegate-type` / `2026-05-02-add-multicast-action` / `2026-05-03-add-event-keyword-multicast` / `2026-05-04-add-event-keyword-singlecast` / `2026-05-04-add-multicast-exception-aggregate`
>
> 跨主题概览见 [`docs/design/`](design/) 各子目录的 `README.md` —— 每个 README 列出当前 phase 状态 + 已落地 spec 引用。
