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
- **pre-1.0 不承诺向后兼容**（与 [`workflow.md` "不为旧版本提供兼容"](../.claude/rules/workflow.md) 对齐）
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

## 当前焦点（next 4–6 周）

**M6 工程支持 + 测试体系 + 错误码体系** + **M7 VM 元数据 + 标准库基础**，对应 SemVer **0.2.x → 0.3.x → 0.4.x**。

### 0.2.x — 工程化 & 包系统 + perf CI 立项

退出标准：`.zbc` v1.x / `.zpkg` 格式冻结；perf CI 上线；多平台 CI matrix 全绿；release 自动化产出跨平台 binary。

| 子版本 | 内容 | 估时 |
|------|------|:----:|
| 0.2.0 | `.zbc` v1.x 格式冻结（magic + section layout 锁定）| 1 周 |
| 0.2.1 | `.zpkg` indexed/packed 格式冻结 + `z42c disasm` 完整 | 1 周 |
| 0.2.2 | Benchmark 套件骨架（`cargo bench` + BenchmarkDotNet）+ 初始基线 | 1.5 周 |
| 0.2.3 | Perf CI + 性能预算（≥10% 退化阻塞 commit）| 1 周 |
| 0.2.4 | `z42c new/init/fmt/clean` 收尾 + `z42-fmt` 独立 binary + `lint-manifest` | 1 周 |
| 0.2.5 | 多平台 CI matrix（5 平台 build/test 全绿）+ CI 模板 | 1.5 周 |
| 0.2.6 | Release 自动化：git tag → 跨平台 z42c/z42vm 二进制 + zpkg 自动产出 | 1 周 |

### 0.3.x — 测试体系 & VM 质量 + GC v1

| 子版本 | 内容 | 估时 |
|------|------|:----:|
| 0.3.0 | Golden 全 L1 覆盖 + interp/JIT 一致性 CI | 2 周 |
| 0.3.1 | 调试符号：行号映射稳定 + 局部变量名 + 栈回溯优化 | 2 周 |
| 0.3.2 | 热重载 VM 端完整实现（interp 模式）| 2 周 |
| 0.3.3 | **GC v1**：抽象 GC 接口 + mark-and-sweep（替换 `Rc<RefCell>`）| 2–3 周 |
| 0.3.4 | Profiler hooks：函数 entry/exit + allocation + GC 事件 | 1 周 |

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
| **0.5.x** | 泛型完整 + Trait 静态分发 + 反射 + LSP v1 + Interop 2a（Rust embedding 稳定）| L3 | 10–14 周 |
| **0.6.x** | 函数式（Lambda / 命名参数 / 模式匹配 / `let` 不可变 / LINQ）+ unmanaged + GC v2 + linter | L3 | 9–11 周 |
| **0.7.x** | `Result<T,E>` + `?` + ADT + `match` 穷尽检查 | L3 | 6–8 周 |
| **0.8.x** | async / await + 多线程 + GC v3（generational + concurrent）+ DAP debugger | L3 | 12–16 周 |
| **0.9.x** | 单文件脚本 + 嵌入 API GA + 可裁剪 + WASM target + Interop 2b（manifest reader / source generator）| L3 | 10–14 周 |
| **0.10.x** | 性能强化（philosophy §9 五指标全部达标）| L3 | 8–12 周 |
| **1.0.x** | 自举（z42 编译 z42 编译 z42 byte-identical）+ 跨架构 NativeAOT + Interop 3 + `z42up` 工具链 GA + SemVer / deprecation 启用 | L3+ | 14–18 周 |

**累计估算**：~16–20 个月（按全职 1 人节奏）。

### 跨版本关键依赖

```
0.1 ─► 0.2 ─► 0.3 ──┬──► 0.4 ──► 0.5 ──► 0.6 ──► 0.7 ──► 0.8 ──► 0.9 ──► 0.10 ──► 1.0
       │       │   │           │                                            │
       │       │   │           └─── reflection ───► 0.5.6 (test 增强)        │
       │       │   │                                                        │
       │       │   └─── GC v1 ─────► GC v2 (0.6) ─► GC v3 (0.8) ───────────► │
       │       │                                                            │
       │       └─── benchmark 套件 ─► perf CI (0.2.3) ──持续生效──────────► │
       │                                                                    │
       └─── .zbc/.zpkg 格式冻结 ─────► 1.0 SemVer 启用 ────────────────────► │
```

强依赖链：
- 0.5 反射 ◄── 0.10 性能数据自查（type metadata access）
- 0.6 unmanaged ◄── 0.9.6 C ABI 头文件
- 0.7 Result ◄── 0.8 async（async fn 通常返回 `Task<Result<T,E>>`）
- 0.8 GC v3 ◄── 0.9.5 VM 组件化
- 0.10 性能基线 ◄── 1.0 稳定承诺
- 1.0 自举 ◄── 0.5+ 全部 L3 特性（编译器自身需 lambda / generic / async）

---

## Feature → Version 映射

每个 features.md 章节落地到哪个 minor。

| features.md 章节 | 所属 minor | 当前状态 |
|------|:------:|:----:|
| §1 Type System / §2 Null Safety / §3 Memory Management / §4 Error Handling (exceptions) / §5 Type Definitions (class/struct/record) / §6 Functions / §7 Control Flow / §8 Strings / §9 Collections / §10 Imports / §11 Numeric Aliases | 0.1.x | ✅ L1 |
| §12 Hot Reload | 0.3.2 | 🟡 设计有；GC v1 后真热更新落地 |
| §13 Execution Mode Annotations | 0.1.x（注解）→ 0.3.x（运行时切换）| 🟡 注解 ✅；运行时切换待 |
| §14 Generics + Trait | 0.5.x | ✅ G1-G4 + L3-Impl 提前落地 |
| §15 Reflection | 0.5.1–0.5.3 | 📋 L3-R 统一批次 |
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
| 当前 | `dotnet build` + `cargo build` + `dotnet test` + `./scripts/test-vm.sh` 全绿 |
| 0.2.3 | Perf CI |
| 0.2.5 | 多平台 CI matrix |
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
| 1.0.0 | 自举 byte-identical + 跨架构 perf 数字 |

---

## 实现里程碑（pipeline 维度）

| 里程碑 | 内容 | 所属阶段 | 状态 |
|--------|------|:-------:|:----:|
| M1 | Lexer + Parser | L1 | ✅ |
| M2 | TypeChecker（L1 特性全覆盖）| L1 → L2 | ✅ |
| M3 | IR Codegen → `.zbc`（L1 特性全覆盖）| L1 → L2 | ✅ |
| M4 | VM Interpreter（L1 特性全覆盖）| L1 | ✅ |
| M5 | VM JIT（Cranelift，L1 特性）| L1 → L2 | ✅ |
| M6 | 工程支持 + 测试体系 + `.zbc` 格式稳定 | L2 | 🚧 当前焦点 |
| M7 | VM 元数据 + 标准库基础（core/io/collections）| L2 | 🚧 当前焦点 |
| M8 | TypeChecker + Codegen 扩展（L3 特性）| L3 | 🟡 部分（泛型 / lambda / delegate 提前）|
| M9 | VM AOT（LLVM/inkwell）| L3 | 📋 |
| M10 | 自举（Self-hosting）| L3+ | 📋 |

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
| Q12 | 0.2.5 | Release artifact 命名（`z42-{version}-{os}-{arch}.tar.gz`？包含哪些 binary？）|
| Q13 | 0.5.7 | LSP server 用 .NET（复用编译器）还是 Rust（复用 VM）？|
| Q14 | 0.8.7 | DAP debugger 多线程暂停语义：单 thread 还是全部？JIT/AOT 如何 step？|
| Q15 | 0.9.7 | WASM 下 GC：等 wasm-gc proposal 还是自实现 wasm-internal GC？|
| Q16 | 0.9.8 | 嵌入式 ~200KB 平台基准（cortex-M4 / esp32 / RISC-V？）|
| Q17 | 1.0-rc6 | `z42up` 用 Rust 还是等自举后用 z42 自身实现？|
| Q18 | 1.x+ | 包注册表中心化（crates.io 模式）还是去中心化（go modules / git URL）？|

---

## Deferred Backlog Index

> 所有显式延后特性的横向索引；条目正文存于对应 design doc 的 "Deferred / Future Work" 段。新增延后项时：① 在对应 design doc 加条目 ② 在本表加索引行。规则见 [`.claude/rules/workflow.md`](../.claude/rules/workflow.md) "延后特性管理"。

### 设计期延后

| 特性 | 描述 | 在哪里 |
|------|------|------|
| L3-G3a 关联类型 | `where T: IAdd<Output=T>` + zbc 扩展 + 运行时校验 | [language/generics.md](design/language/generics.md) |
| 闭包档 A 完整版 | 任何不逃逸 closure 栈分配（当前仅单变量子集）| [language/closure.md](design/language/closure.md) |
| 闭包档 B 完整版 | 单态化 + 泛型形参标注（当前仅 alias 子集）| [language/closure.md](design/language/closure.md) |
| 闭包档 C send 派生 | 与 concurrency 实施一起做 | [language/closure.md](design/language/closure.md) |
| Static abstract iter 2+ | 类型级访问（`T.Zero` / `T.Parse(s)`）| [language/static-abstract-interface.md](design/language/static-abstract-interface.md) |
| MulticastFunc/Predicate 异常聚合 | D2d-2 Func/Predicate 路径（D2d-2-Action 已落地）| [language/delegates-events.md](design/language/delegates-events.md) |
| ref local / return / field / struct | parameter-modifiers D1-D4 | [language/parameter-modifiers.md](design/language/parameter-modifiers.md) |
| StackTrace / 构造器重载 / 字段 ? 标注 / self-assign | exceptions Phase 1 限制 | [language/exceptions.md](design/language/exceptions.md) |
| Layer 3 用户定义 operator/keyword | customization 第三层 | [language/customization.md](design/language/customization.md) |
| foreach IEnumerator 路径 | 升级为接口 dispatch（当前仅鸭子协议）| [language/iteration.md](design/language/iteration.md) |
| 自定义 body / init-only / expression-bodied property | properties 未支持子集 | [language/properties.md](design/language/properties.md) |
| Tier 2/3 完整 interop | manifest reader / 源生成 / symbol resolution | [language/interop.md](design/language/interop.md) |
| 整体 L3 concurrency | async/await / Future / Send-Sync / 调度器 | [runtime/concurrency.md](design/runtime/concurrency.md) |
| hot-reload 签名变更 + 跨模块 | 签名变更检测 / 跨模块 reload 故事 | [runtime/hot-reload.md](design/runtime/hot-reload.md) |
| 完整 JIT 指令映射 + 性能基准 | jit.md 待补 | [runtime/jit.md](design/runtime/jit.md) |
| GC handle Phase 3+ | Pinned / WeakTrackResurrection / 多线程 barrier | [runtime/gc-handle.md](design/runtime/gc-handle.md) |
| stdlib P0–P3 缺失包 | time / fs / threading / encoding / net | [stdlib/roadmap.md](design/stdlib/roadmap.md) |
| split-debug-symbols 退化 trace ip+build_id | line==0 时帧追加 `+0x<ip> [build:<8hex>]`；需 VmFrame 追踪 PC | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| `z42c symbolicate` 离线工具 | 把 `.zsym` 应用到 crash trace 还原 file:line:col | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| sidecar lazy / mmap 加载 | 启动延迟敏感场景的优化路径 | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| sidecar 跨目录搜索 | debuginfod 风格 + 环境变量配置 | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |
| `Std.Reflection.Symbolicate` 公开 API | 程序内触发符号化 | [language/exceptions.md](design/language/exceptions.md#deferred--future-work) |

### 实施期延后（D-* 系列）

| ID | 标题 | Design doc 条目 |
|------|------|------|
| **D-2** | ISubscription chain `.AsOnce()` / `.AsWeak()` 跨 generic interface impl | [language/delegates-events.md](design/language/delegates-events.md#d-2-isubscription-chain-asonce--asweak-跨-generic-interface-impl) |
| **D-3** | N>4 arity Action / Func（自举后用 z42 写生成器）| [language/delegates-events.md](design/language/delegates-events.md#d-3-n4-arity-action--func) |
| **D-4** | 协变 / 逆变（`<in T, out R>` 等）| [language/generics.md](design/language/generics.md#d-4-协变--逆变in-t-out-r-等) |
| **D-11** | introduce-bound-visitor（review.md §2.1 visitor 抽象基类）| [compiler/compiler-architecture.md](design/compiler/compiler-architecture.md#d-11-introduce-bound-visitorreviewmd-21-visitor-抽象基类) |
| **D-12** | BindCall 函数级拆分（split-typechecker-calls 残留）| [compiler/compiler-architecture.md](design/compiler/compiler-architecture.md#d-12-bindcall-函数级拆分split-typechecker-calls-残留) |

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
