# z42 Roadmap

> 本文按 **phase（L1/L2/L3）** 组织实现进度，回答"现在做到哪一步"；版本发布节奏按 **SemVer** 见 [version.md](version.md)。
> 已完成 spec 的实施细节存于 [docs/spec/archive/](spec/archive/)；按主题查规范见 [docs/design/](design/)。

## 固定决策

- **GC**：z42 始终带 GC，不引入所有权/借用（降低上手成本）
- **IR**：寄存器 SSA 形式
- **执行模式注解**：作用于命名空间级
- **`.zbc` magic**：`ZBC\0`

---

## 阶段总览

| 阶段 | 目标 | 状态 |
|------|------|------|
| **L1** | C# 基础子集，跑通完整 pipeline（源码 → IR → VM 执行） | ✅ 已完成（详见下） |
| **L2** | 基础设施完善（编译、工程、测试、VM 质量、标准库） | 🚧 进行中 |
| **L3** | 高级语法扩展（泛型、Lambda、异步 + z42 特有特性） | 🟡 部分（泛型 + lambda + delegates 已落地，async / ADT / Result 待开始） |

> 阶段严格串行：L1 pipeline 全通 → 启动 L2；L2 全完成 → 启动 L3。当前 L1 全绿、L2 多项进行中、L3 部分提前落地。

**当前焦点**：M6（工程支持 + 测试体系 + 错误码体系）→ M7（VM 元数据 + 标准库）。下一阶段对应 SemVer 版本：[0.2.x（工程化 & 包系统）](version.md#02x--工程化--包系统--perf-ci-立项) → [0.3.x（测试体系 & VM 质量）](version.md#03x--测试体系--vm-质量--gc-v1)。

---

## L1 — Bootstrap（C# 基础子集）✅

**目标已达成**：以最小特性集跑通完整 pipeline（词法 → 语法 → 类型检查 → IR Codegen → VM 执行）。

**特性范围**：基本类型 + 数值别名 / 全部运算符 / 控制流（if/while/do-while/for/foreach/switch）/ 三目 + null-coalesce + null-conditional / 字符串插值 / 数组 + List + Dictionary（pseudo-class）/ 可空类型 / 枚举 / 类（字段/构造器/方法/auto-property/static）/ struct / record / 接口 / 继承 / 异常 / 默认参数 / `ref/out/in` 参数修饰符 / `[ExecMode]` + `[HotReload]` 注解。详见 [language-overview.md](design/language/language-overview.md) 与各专题 [design/language/](design/language/)。

实施明细按 spec 归档于 [docs/spec/archive/2026-04-04-* 至 2026-05-05-*](spec/archive/)。

---

## L2 — Foundation（基础设施）🚧

**目标**：在 L1 pipeline 基础上，补全编译器覆盖、稳定工程体系、建立测试基线、提升 VM 质量，落地基础标准库。

### 编译器完善

- **TypeChecker / IrGen**：✅ L1 全特性覆盖
- **错误体系**：✅ E####（C# 编译期）+ Z####（Rust runtime）catalog 已就位；🚧 友好错误消息 + `z42c explain <CODE>` 命令进行中
- **`.zbc` 二进制格式**：✅ v1.x 稳定（magic / version / section layout 锁定）；🟡 split-debug-symbols Phase 4 进行中（per-param 类型名带入 SIGS）
- **`disasm` 反汇编**：✅ 基础可读

### 工程支持 ✅

- `z42.toml` 单包 manifest + workspace（virtual + member + include + policy + 集中产物）
- `z42c new/init/build/check/run/clean/fmt/disasm/explain/info/metadata/tree/lint-manifest`
- source_hash 增量编译（命中跳过 parse + typecheck + irgen）
- 详见 [`compiler/project.md`](design/compiler/project.md) + [`compiler/compilation.md`](design/compiler/compilation.md)

### 测试体系 ✅（部分）

- ✅ Golden test 全 L1 覆盖（114 vm_core + 20 stdlib-bound = 134；按 dotnet/runtime-style 重组）
- ✅ Interp + JIT 双模式跑同一测试集结果一致
- ✅ CI 门禁：`dotnet test` + `test-vm.sh` + `test-stdlib.sh` + `test-cross-zpkg.sh` 全绿
- ✅ R 系列基础（R1 TIDX section / R2 z42.test 库 + Bencher / R3 z42-test-runner subprocess + format/filter / R3c test-changed / R4 attribute 校验 + 泛型 attribute / R5 stdlib 各库本地测试）
- 📋 R3b：in-process runner + Setup/Teardown hook 真生效 + Bench 调度模式 + zpkg-as-input

详见 [`testing/testing.md`](design/testing/testing.md)。

### VM 质量

- ✅ Type metadata（type info / 字段布局 / 方法表）
- 📋 调试符号：行号映射、局部变量名（split-debug-symbols 系列进行中）
- 🚧 Interpreter 优化（指令 dispatch / 对象分配路径）
- 🚧 JIT 优化（热点函数识别 / 简单内联 / 常量折叠）
- ✅ MagrGC 子系统（trait + Bacon-Rajan 环回收 + Drop-time finalizer + external root scanner + interp/JIT 栈扫描 + strict OOM）
- ✅ GC stdlib 重组（`Std.GC` / `WeakHandle` / `GCHandle` / `HeapStats` 暴露脚本端）

详见 [`runtime/vm-architecture.md`](design/runtime/vm-architecture.md) + [`runtime/gc-handle.md`](design/runtime/gc-handle.md)。

### Native Interop / 三层 ABI ✅

C1 接口骨架 → C2 Tier 1 C ABI runtime → C3 Tier 2 ergonomic Rust API → C4-C8 pinned/byte-buffer/CStr marshal → C9 类级 native shorthand → C10 Array<u8> pin → C11a-e import-from 语法 + manifest reader + opaque-handle whitelist。

后续未排：C11c (Path B2 脚本字段 + VM `z42_obj_*` ABI) / C11d (Path C `[Repr(C)]`) / C11f (c_char return ownership / Array/Option / 定长数组) / `extern class T` / `CallNativeVtable` runtime + IR codegen / JIT emit native opcodes。

详见 [`language/interop.md`](design/language/interop.md)。

### Embedding / Hosting API ✅（H0-H3）

H0 设计 → H1 C ABI scaffold（initialize/shutdown/last_error）→ H2 hello-world 全链路（load_zbc + resolve_entry + invoke + corelib + import_namespaces + stdout sink + Tier 2 `z42-host` crate + `examples/hello_rust`）→ H3 错误路径覆盖。

| Spec | 内容 | 状态 |
|------|------|------|
| **H0**（add-embedding-api design ✅ 2026-05-10）| docs/spec/changes 四件套；三层 ABI；多实例 / hot-reload / GC handle / async / Tier 3 facade 进 Deferred | ✅ |
| **H1-H3** | Tier 1 / Tier 2 + 错误路径全覆盖 | ✅ |
| **H4** | iOS Swift facade / Android JNI bridge | 📋 |
| **H5** | `z42-test-runner` 重构基于 `z42-host` | 📋 |

详见 [`runtime/embedding.md`](design/runtime/embedding.md)。

### 标准库（基础）

- ✅ `z42.core`：Object / 基本类型 / Type / Convert / Assert / Exception 树（9 标准子类）/ `IDisposable` / `IEnumerable<T>` / `IEnumerator<T>` / `IComparable<T>` / `IEquatable<T>` / `IComparer<T>` / `IEqualityComparer<T>` / `IFormattable` / `INumber<T>`（Wave 1-3）
- ✅ `z42.core/Collections/`：`List<T>` / `Dictionary<K,V>` 纯脚本（pseudo-class 已替换）
- ✅ `z42.core/Delegates/`：Action / Func / Predicate（0-4 arity，详见 [delegates-events.md](design/language/delegates-events.md)）
- ✅ `z42.core/GC/`：GC / WeakHandle / GCHandle / HeapStats
- ✅ `z42.collections`：Stack / Queue / LinkedList
- ✅ `z42.io`：Console / File / Path / Environment（host FFI L2 例外）
- ✅ `z42.math`：libm 绑定 + 常量
- ✅ `z42.text`：StringBuilder（纯脚本）
- ✅ `z42.test`：z42 测试框架（Assert / TestIO / Bencher / TestFailure / SkipSignal）

📋 缺失包（按 P0–P3 排期）：见 [`stdlib/roadmap.md`](design/stdlib/roadmap.md)。

### 代码质量 Backlog（按触发条件执行）

> 已完成批次 1–4 见 spec/archive/；以下为低优先级未完成项。

| 项目 | 触发条件 | 说明 |
|------|---------|------|
| A6: Value `Rc<RefCell>` → `Arc<Mutex>` 或对象池 | L3 async/线程模型设计时 | `Rc` 是 `!Send`；MagrGC Phase 3 切换 `GcRef<T>` 时一并解决 |
| A10: `PackageCompiler` → 可注入 `BuildPipeline` | mock 文件系统单元测试时 | 当前 static class 可用，低优先级 |
| `TypeEnv.BuiltinClasses` 动态注入 | 泛型类型表示扩展时 | 当前硬编码集合 |
| `IsReferenceType` 中 List/Dict 硬编码 | 同上 | List/Dict 应为 `Z42ClassType` |
| switch 穷举检查（exhaustiveness） | enum switch 场景增多时 | switch on enum 不检查覆盖 |
| 死代码警告 | IDE 集成或用户反馈时 | return 后语句静默丢弃 |
| 隐式窄化转换拒绝 | 数值精度 bug 出现时 | `int x = someLong` 应显式 cast |
| `IrInstr` JsonDerivedType 自动注册 | 指令数超过 60 个时 | 当前 54 个注解 |
| `exec_instr.rs` 按类别拆分辅助函数 | 文件超过 450 行时 | 当前 362 行 |
| Golden Test 改用 `test.toml` 声明类别 | 测试目录结构变复杂时 | 当前路径约定（`/errors/`, `/run/`）够用 |

---

## L3 — Advanced（高级特性）🟡

**目标**：引入 L1 推迟的高级语法 + z42 特有类型系统扩展。L2 全完成后启动；部分子项已提前落地（泛型 / lambda / delegate）。

### L3-G 泛型 ✅（核心已完成）

| 子阶段 | 内容 | 状态 |
|--------|------|:----:|
| **L3-G1** | 泛型函数 + 泛型类（无约束） | ✅ |
| **L3-G2** | 接口约束 `where T: I + J` | ✅ |
| **L3-G2.5** | 约束范式（基类 / ctor / class / struct / enum / 接口继承 / 裸 type-param 链 / 数值 / operator / static abstract iter 1）| ✅ |
| **L3-G2.5 残余** | `notnull` / `unmanaged` / `reified` / `Func` 约束 / 关联类型链 / `<in/out>` 变型 / 默认 type-param | 📋 大部分进 Deferred |
| **L3-G3a** | zbc 约束元数据 + VM loader 加载时校验 | ✅ |
| **L3-G3c** | 关联类型 | ⏸ Deferred（见 [generics.md](design/language/generics.md)）|
| **L3-G3d** | 跨 zpkg TSIG 约束传播 | ✅ |
| **L3-G4a-h** | 泛型容器 + 索引器 + ArrayList/HashMap + foreach 鸭子协议 + pseudo-class 替换 | ✅ |
| **L3-Impl1/2** | extern impl + 跨 zpkg 传播 | ✅ |
| **L3-R** | 反射 + 运行时类型信息 | 📋 统一批次延后（见 §L3-R 下节）|

详见 [`generics.md`](design/language/generics.md) + [`static-abstract-interface.md`](design/language/static-abstract-interface.md)。

### L3-C 闭包 / Lambda ✅（核心 + JIT 完成）

| 子阶段 | 内容 | 状态 |
|--------|------|:----:|
| **L2-C1** lambda 字面量 / `(T)->R` 函数类型 / `Func<>`/`Action<>` desugar | ✅ |
| **L2-C1b** local function | ✅ |
| **L3-C2-core** 捕获 + 档 C 堆擦除 | ✅ |
| **L3-C2-loops** 循环变量新绑定（值快照自动满足）| ✅ |
| **L3-C2-jit** JIT 路径补全 | ✅ |
| **L3-C2-mono** 档 B 完整版（单态化）| 📋 当前仅 alias 子集 |
| **L3-C2-stack** 档 A 完整版（栈上 env）| 📋 当前仅 callee 立即调用子集 |
| **L3-C2-send** Send 派生 + spawn 检查 | ⏸ 与 concurrency 同期 |

详见 [`closure.md`](design/language/closure.md)。

### L3-D Delegates / Events ✅

D1（delegate 关键字 + 单播 + 方法组缓存）+ D2a/b/c/d-1/d-2-Action（多播 + ISubscription wrapper + event 关键字 + 异常聚合）+ D-1a/b（WeakHandle / WeakRef wrapper）+ D-5（ReferenceEquals）+ D-6（嵌套 dotted-path）+ D-7（单播 IDisposable token + 严格 access control）。

📋 D2d-2 Func/Predicate 异常聚合（Action 路径已完成）；D-2 ISubscription chain；D-3 N>4 arity（Deferred 见 [delegates-events.md](design/language/delegates-events.md)）。

### L3-R 反射与运行时类型信息（统一批次，延后）

合并 L3-G3b、运行时约束校验、`new T()` / `Activator.CreateInstance<T>` 等需求，一次性规划 VM 类型系统。R-5 `type_args` 运行时传递机制是核心架构决策。

**先决条件**：✅ L3-G3a 元数据；📋 L3-G3c 关联类型；📋 VM 架构决策。

详见 [generics.md](design/language/generics.md) §L3-R。

### 高级语法（待开始）

| 特性 | 说明 |
|------|------|
| `async` / `await` | 染色 async；`Task` / `ValueTask`；结构化并发；与 GC v3 + concurrency 同期 |
| LINQ 风格 | `Where` / `Select` / `OrderBy` 等（依赖 lambda + IEnumerable）|
| 命名参数 | call-site `Greet(name: "z42")` |
| 模式匹配扩展 | 属性模式 / 位置模式 / `is` 类型测试 |

### z42 特有扩展（待开始）

| 特性 | 说明 |
|------|------|
| `Result<T, E>` + `?` | 函数式错误处理（与 try/catch 共存）|
| `Option<T>` | 替代 `T?`（编译期穷尽检查）|
| Trait 静态分发 | 替代 vtable 接口分发 |
| ADT（代数数据类型） | 原生 sum type（替代 abstract record 模拟）|
| `match` 穷尽检查 | 替代 `switch` |
| 默认不可变变量 | `let` 不可变 / `var` `mut` 显式可变 |
| 单文件脚本模式 | 无 `z42.toml` 直接 `.z42` 执行 |
| 内联 eval | `z42vm -c "..."` + 嵌入 API source 输入 |
| REPL | 交互式求值 |

---

## 实现里程碑（pipeline 维度）

| 里程碑 | 内容 | 所属阶段 | 状态 |
|--------|------|:-------:|:----:|
| M1 | Lexer + Parser | L1 | ✅ |
| M2 | TypeChecker（L1 特性全覆盖） | L1 → L2 | ✅ |
| M3 | IR Codegen → `.zbc`（L1 特性全覆盖） | L1 → L2 | ✅ |
| M4 | VM Interpreter（L1 特性全覆盖） | L1 | ✅ |
| M5 | VM JIT（Cranelift，L1 特性） | L1 → L2 | ✅ |
| M6 | 工程支持 + 测试体系 + `.zbc` 格式稳定 | L2 | 🚧 |
| M7 | VM 元数据 + 标准库基础（core/io/collections） | L2 | 🚧 |
| M8 | TypeChecker + Codegen 扩展（L3 特性） | L3 | 🟡 部分（泛型 / lambda / delegate）|
| M9 | VM AOT（LLVM/inkwell） | L3 | 📋 |
| M10 | 自举（Self-hosting） | L3+ | 📋 |

---

## Deferred Backlog Index

> 所有显式延后特性的横向索引；条目正文存于对应 design doc 的 "Deferred / Future Work" 段。新增延后项时：① 在对应 design doc 加条目 ② 在本表加索引行。规则见 [`.claude/rules/workflow.md`](../.claude/rules/workflow.md) "延后特性管理"。

### 设计期延后（feature 设计图景内的"暂不引入"）

各 design doc 的 "Deferred / Future Work" 段直接维护，本表选择性索引最有价值的项。

| 特性 | 描述 | 在哪里 |
|------|------|------|
| L3-G3a 关联类型 | `where T: IAdd<Output=T>` + zbc 扩展 + 运行时校验 | [language/generics.md](design/language/generics.md) |
| 闭包档 A 完整版 | 任何不逃逸 closure 栈分配（当前仅单变量子集） | [language/closure.md](design/language/closure.md) |
| 闭包档 B 完整版 | 单态化 + 泛型形参标注（当前仅 alias 子集） | [language/closure.md](design/language/closure.md) |
| 闭包档 C send 派生 | 与 concurrency 实施一起做 | [language/closure.md](design/language/closure.md) |
| Static abstract iter 2+ | 类型级访问（`T.Zero` / `T.Parse(s)`） | [language/static-abstract-interface.md](design/language/static-abstract-interface.md) |
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
