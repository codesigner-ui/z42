# Design: 0.4.x 四流并行编排

## Architecture

```
0.3.x 自举线（收尾）
        │
        ▼
0.4.x 质量与性能线 ── 4 主线并行 + G 前置流
        │
   ┌────┼────┬────────┬────────┐
   P    B    S        L        G(前置)
 perf bench syntax   lib    泛型实例化
   │    │    │        │        │
   │    │    │        └── Deserialize<T> ◄── G2/G3 泛型反射
   │    └── baseline ◄── P 每个杠杆配 bench 回归
   │
   ▼
退出标准达成 → 0.5.x（泛型完整 + Trait + LSP + Interop 2a）
```

## Decisions

### Decision 1: 沿用 0.3.x 多主线并行，而非线性填包

**问题**：原 0.4.x 线性"填 stdlib 包"已与现实脱节（24 包已 ship）。
**选项**：A 保留线性框架塞进 perf/syntax；B 4 流并行；C 混合（stdlib 为主 + perf/bench 横切）。
**决定**：选 B（User 2026-06-23 裁决）。4 流天然映射 4 个独立子系统锁，可真并行；线性框架会把正交的 perf/syntax 串死。

### Decision 2: 完整泛型 serde → 新增 G 前置流（接受 L3 提前例外）

**问题**：`Deserialize<T>()` 自动绑定任意类型依赖运行期泛型实例化 + 泛型反射，二者原排 0.5.x。
**选项**：A 仅流式 reader（serde 留 0.5.x）；B 非泛型特性绑定（依赖 0.3.12 已落地 Invoke）；C 硬上完整泛型 serde（提前拉 0.5.x L3-R）。
**决定**：选 C（User 2026-06-23）。代价：违反 roadmap "不为单点提前半个 L3"，把泛型实例化 + 泛型反射三件套提前。缓解：**显式登记为 G 流招牌前置（非偷塞 JSON 子任务）+ 两步交付**（先非泛型保产物，G 就绪再上泛型版）。0.5.x 反射条目相应清空。

### Decision 3: bench 流先行于 perf 流

**问题**：P 流兑现杠杆必须能度量收益、防回退。
**决定**：B 流的 baseline 铺面（B5）+ `z42c bench` GA（B1/B2）排在 P 流主体之前；P 每兑现一个杠杆，先有对应 baseline，落地后 B3 收紧成硬门禁。

### Decision 4: P 流不含 OSR/deopt

**问题**：tiered-execution.md 的 JIT 分层与 hot-reload 共用 OSR/deopt 地基工作量大。
**决定**：0.4.x 只做不依赖 deopt 的杠杆（拆箱 / inline cache / 折叠 / 非原子 RC / devirt）；OSR/deopt 留 0.5.x。理由：0.10.x 才需全部兑现，0.4.x 取高 ROI 子集。

### Decision 5: 锁协调

**问题**：`stdlib` 锁被 L 流 + P6（脚本 perf）+ B5（baseline）三处争用；`compiler`/`z42c` 被 S 流 + G 流同时吃。
**决定**：`stdlib` 三处串行排队（parallel-development.md）；S 流与 G 流串行或合并节奏（Q-B 待裁决）。子版本号弹性，按锁可用性排。

## Implementation Notes

- 本变更产出**规划文档**，不写实现代码。各子版本（0.4.0–0.4.6）在启动时按 workflow 阶段 1–9 逐个开独立 spec。
- perf 杠杆出处映射：P1 jit.md Phase 2；P2 tiered-execution.md §3；P3 ir-specialization.md §2–3；P4 vm-architecture.md（memory project_jit_perf_progress）；P5 ir-specialization.md §1。
- G 流出处：roadmap 原 0.5.x L3-R + L3-G；reflection.md Deferred 段「嵌套泛型 / 泛型方法 Invoke」。

## P 流细化：编译器侧 + VM 侧框架与性能

> 2026-06-23 补充。P 流不是单一 `runtime` 工作，而是横跨两套代码框架：**编译器**（C# `src/compiler/` + 自举 `src/z42c/`，byte-identical 约束）与 **VM**（Rust `src/runtime/`）。两侧吃不同锁，可并行；但编译器侧改 codegen 必须双侧镜像。

### 已落地基线（不重复做，0.4.0 只做量化刻画）

| 项 | 落地 | 出处 |
|----|------|------|
| 4-slot 多态 IC（FieldIC + VCallIC，mono 零开销 / poly round-robin）| 2026-05-28 | `metadata/resolver.rs` + `jit/helpers/vcall.rs` |
| JIT I64 helper 类型特化（`jit_add_i64` 等 12–15 变体，1.5–1.8× 算术循环）| 2026-05-28 | `jit/translate.rs` |
| cross-zpkg 调用 OnceLock 缓存（3–5× 跨模块调用）| 2026-06-11 | `interp/exec_call.rs` `ResolvedTokens.cross_module_targets` |
| `Instruction` enum 96B→32B（冷变体装箱，热算术内联）| 2026-06-11 | `metadata/bytecode.rs` |
| GC v1 三阶段（mark-sweep + 试删环检 + 分代/并发可选 + safepoint）| 2026-05-22 | `gc/arc_heap.rs` |
| 部分 opcode 特化（`String.Length`→`StrLen`）| — | `interp/exec_value.rs` |
| Bound visitor 框架（D-11，部分）| 2026-05-10 | `z42.Semantics/Bound/BoundExprVisitor.cs` |

### VM 侧（Pv，吃 `runtime` 锁）

VM 框架现状：`interp`（穷尽 `match` on `Instruction` enum 派发）/ `jit`（Cranelift，现 default，全 Value 操作走 `extern "C"` helper）/ `gc`（`MagrGC` trait，`GcRef<T>` Arc-backed）/ `corelib`；`Value` 13 变体 tagged enum（~24B，重变体装箱）。

| ID | 杠杆 | 现状 | 落点 | 收益 / 阻塞 |
|----|------|------|------|------|
| **Pv1** | quickening + 超指令（首次执行后改写 `FieldGet{name}`→`FieldGetAtOffset{offset}`；融合常见 opcode 序列）| 设计（tiered-execution §3）| `interp/mod.rs::exec_function` 循环 | interp 15–25% 热路径；阻塞=字节码改写安全协议（版本/异常安全）|
| **Pv2**（招牌）| JIT 直接 Cranelift `iadd` emit（已知 I64 时跳过 helper）+ F64 特化对称补齐 | I64 helper 特化已落地；直接 emit 未做、F64 deferred | `jit/translate.rs::translate_instr` case Add | 算术内循环 2–3×；**依赖类型收窄信息**（来自 Pc2 intrinsic/类型标注或 IC type cache）|
| **Pv3** | Frame 寄存器 `HashMap<u32,Value>` → 稠密 `Vec<Value>` | 当前 HashMap（每次 get/set 哈希）| `interp/mod.rs` `Frame` 结构 | 全 interp 路径常数因子；寄存器号本就稠密连续，HashMap 是结构性浪费 |
| **Pv4** | 单线程路径非原子 refcount（`Arc`→`Rc` feature-gate）| 设计 | `metadata/types.rs::Value` + `gc/refs.rs` 后端选择 | string-heavy 10–15%；**门控=profiling 证明 refcount >5% 占比**（否则不做）|
| **Pv5** | devirt 完整（IC type_id 驱动 mono 站点直接函数指针 + 推测内联）| IC memo + primitive-as-struct 已部分 | `jit/helpers/vcall.rs` | call-heavy 5–10%；完整推测内联**依赖 deopt → 留 0.5.x** |
| **Pv6** | StringId interning（`ConstStr` 的 `Box<str>`→`u32`）| 设计（slim-instruction-stringid）| `metadata/bytecode.rs` + loader + codegen | 再省 ConstStr ~12B；阻塞=zbc bump + fixture regen 授权（与 Pc 双侧镜像耦合）|

### 编译器侧（Pc，吃 `compiler`+`z42c` 锁，byte-identical 双侧镜像）

编译器框架现状：7 工程 pipeline（Core/Syntax/Semantics/IR/Project/Pipeline/Driver）；**`IrPassManager` 框架已搭好但零 pass**（`z42.IR/IrPassManager.cs`，仅 `NoOpPass` 占位）；intrinsic 表设计就绪未实现（ir-specialization.md §2.2）；大类待拆（`ZbcWriter.cs` 891L / `PackageCompiler.BuildTarget.cs` 866L / `TypeChecker.Calls.cs` 的 `BindCall` ~395L 超 60L 硬限）。

| ID | 杠杆 | 现状 | 落点 | 收益 / 约束 |
|----|------|------|------|------|
| **Pc1** | 激活 `IrPassManager` 首批 pass：常量折叠（含 intrinsic 折叠 `"s".Length`→3）+ DCE | 框架空 | `z42.IR/IrPassManager.cs` + 新 pass 类 | 启动 + literal-heavy；**改 emit → 必须 z42c 镜像**保 byte-identical |
| **Pc2** | intrinsic 表（统一 `(Type,Member)→{pure,const_fold,interp_op,jit_lower}`）+ devirt pass（sealed/已知类型 VCall→直接 Call）| 设计就绪 | IrGen.cs（emit 前查表）| 喂 Pv2 类型收窄 + 消虚调用；双侧镜像 |
| **Pc3** | 大类拆分 + `BindCall` D-11 visitor 抽取（独立 refactor commit）| D-11 部分 | TypeChecker.Calls.cs / ZbcWriter.cs / PackageCompiler | 代码组织（200 行硬限）；解锁方法级 perf 分析 |
| **Pc4** | compile-perf phase profiling（Lexer/SymbolCollector/TypeChecker/IrGen/ZbcWriter 逐阶段计时）+ 已知热点（`FinalizeInheritance` O(N²)、`QualifyClassName` 重复解析、import 符号缓存）| 无 profiling hook | Pipeline + SymbolCollector | 喂 0.3.10「median ≤3× C#」gate；定位真瓶颈再优化 |
| **Pc5** | 并行编译（多 CU 的 collect+typecheck 并行后再串行 IrGen/ZbcWriter）| 串行 | PackageCompiler | 多文件包吞吐；保 ZbcWriter 确定性（资源加载排序，common-pitfalls §1）|

### 关键耦合（排期约束）

1. **Pv2 ◄── Pc2**：JIT 直接 emit 拆箱需要"此处必为 I64/F64"的类型信息；最干净来源是编译器 intrinsic 表/类型标注，否则只能靠运行期 IC 收窄（次优）。故 Pc2 宜略先于或同步 Pv2。
2. **Pc1/Pc2/Pc6 改 codegen ⇒ z42c 双侧镜像**：任何改变 emit 字节的 pass 必须同步 `src/z42c/`，否则 0.3.10 byte-identical gate 红。这是 Pc 的硬约束，也是 Pc 与 S/G 串行争 `z42c` 锁的原因。
3. **deopt 是 0.5.x 边界**：Pv5 完整推测内联 + interp t0↔t1 OSR 都依赖 OSR/deopt 框架 → 0.4.x 只做不依赖 deopt 的子集，框架本身留 0.5.x（已写入 0.5.x 主题）。
4. **每个 Pv/Pc 杠杆配 B 流 baseline**：落地前先有 bench（B5），落地后 B3 收紧硬门禁，证明收益且防回退。

## Testing Strategy

- 本变更为纯文档/规划，验证 = roadmap 内部引用一致性（无悬挂引用、0.4.7/0.5.x 反射条目移除后无残留指向）。
- 各子版本 spec 落地时各自带 GREEN（perf 配 bench 回归、syntax 配 golden、lib 配 [Test]）。
- Pc 类（改 codegen）额外验证：`z42c` 双侧 byte-identical gate 不回退 + compile-perf gate median ≤3× C#。
