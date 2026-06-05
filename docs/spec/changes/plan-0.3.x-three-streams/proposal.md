# Proposal: 0.3.x 三主线规划（stdlib 整理 + 自举启动 + 反射 MVP）

> 状态：📋 规划已审批（2026-06-05）｜类型：roadmap 重排｜责任人：User + Claude

## 背景

0.2.0 发布前期工作已收尾。User 决定将 0.3.x 重排为三条并行主线：

1. **标准库整理 + 性能**（"模块划分和性能都不是很好"）
2. **编译器自举**（"可以并行，等全部实现了在进行替换"）
3. **反射 MVP**（从原 0.5.1–0.5.3 提前到 0.3.x）

原 0.3.x 五项（Golden 全 L1 覆盖 / 调试符号 / 热重载 / GC v1 / Profiler）全部推 0.4.x 起，唯有 **GC v1 提前到 0.3.0** 作为三主线共同前置。

## User 已裁决

| 决策点 | 裁决 |
|------|------|
| B 主线 0.3.x 范围 | 仅 Lexer + Project + Driver + Parser 四个易做子系统 |
| C 主线 0.3.x 深度 | 只读元数据 + typeof / GetType + Attribute reflection |
| GC v1 时机 | **前置到 0.3.0**（A/B 共同前置） |
| 原 0.3.x 项处理 | 全推 0.4.x 起 |

## 三主线概览

### A — 标准库整理 + 性能

**目标**：22 个包的模块划分梳理（合并 / 拆分 / 重命名）+ 命名空间一致性 + 公开 API 边界 + 数据驱动的 perf 攻坚（不靠拍脑袋）

**子版本**：A0 spec → A1 重组 → A2 bench baseline → A3/A4/A5 三轮 perf 攻坚

**已识别 perf 候选**（需 A2 bench 数据确认排序）：

- BigInt：Karatsuba（[numerics.md Deferred](../../../design/stdlib/numerics.md)）
- List / Dict 热路径（[collections.md](../../../design/stdlib/collections.md)）
- String / StringBuilder
- Path / Encoding
- JSON / YAML / TOML reader（共用 lexer 抽取）

**已识别组织争议**（A0 spec 需裁决）：

- Console placement：z42.io vs z42.core prelude（[organization.md](../../../design/stdlib/organization.md) 草案中三个候选）
- 命名空间一致性：`Std.IO.binary` vs `Std.IoBinary`
- 22 包中是否有可合并 / 可拆分项

### B — 编译器自举（**0.3.x 不完成**，详见 [B 主线深度规划](#b-主线深度规划编译器自举)）

**0.3.x 目标**：完成无 L3 依赖的 4 个易做子系统 + 建立**逐子系统 bit-identical 验证 CI gate** + **每子系统 micro-bench**（throughput 对比 C# 实现）

**B 主线四子系统**：

| 子系统 | 现有 C# 对应 | L3 依赖 | 难点 |
|------|-------------|:----:|------|
| Lexer | z42.Syntax/Lexer/* | 无 | 字符迭代；现有 z42.text Char API 足够 |
| Project manifest reader | z42.Project/ProjectManifest.cs | 无 | z42.toml 已有 TOML reader |
| Driver CLI | z42.Driver/Program.cs | 无 | z42.cli arg parsing 已有 |
| Parser | z42.Syntax/Parser/* | 部分（用虚方法替代 visitor） | AST 节点改 class；下推 visitor pattern；代码量大 |

**剩余子系统推迟到 0.5.x**：

- Semantic：visitor pattern 强依赖 lambda（L3-C）
- TypeChecker：AST 集合强依赖 generic（L3-G）
- IR builder / lowering：同上
- ZbcWriter / ZpkgWriter：依赖 generic 元数据
- Pipeline：依赖上述

**CI gate（0.3.x）**：每子系统 z42 实现产物（token stream JSON / AST JSON / manifest 解析结果 / CLI 错误码输出）与 C# 实现产物**逐字节对账**。任一字节差异 → CI 红。

**0.3.x 退出**：4 个易做子系统 CI bit-identical 对账 7 日内零飘移 + 每子系统 micro-bench baseline 入库（throughput vs C#）。

### C — 反射 MVP

**0.3.x 范围**：

- C0 Spec：`Type` / `MethodInfo` / `FieldInfo` / `PropertyInfo` / `ParameterInfo` API 形状；与 zpkg `TypeDesc` / `MethodDesc` 元数据的映射
- C1：runtime 暴露 `Type.GetMembers / GetMethods / GetFields / GetProperties` 系列
- C2：`typeof(T)` 编译器关键字 + `obj.GetType()` runtime intrinsic + z42.reflection 包公开
- C3：Attribute reflection（前置：**用户自定义 attribute 机制 spec 需先落地**）

**0.3.x 不做**（推 0.5.x L3-R 完整版）：

- `Method.Invoke` / `Activator.CreateInstance<T>()` / `Type.MakeGenericType` — 强依赖 generic instantiation

## 0.3.x 子版本编排

```
0.3.0  GC v1（A/B/C 共同前置）

0.3.1  A0 spec + B0 spec + C0 spec（三主线 spec 同步起草）

0.3.2  A1 包结构重组（重组目录 + namespace + 调用点全量更新）

0.3.3  A2 bench baseline ║ B1 Lexer in z42 ║ C1 metadata 暴露

0.3.4  A3 perf #1 BigInt/Coll ║ B2 Project manifest ║ C2 typeof + GetType

0.3.5  A4 perf #2 String/IO ║ B3 Driver CLI ║ C3 Attribute reflection
       （前置：attribute 机制 spec 在 0.3.4 已起草）

0.3.6  A5 perf #3 JSON/YAML/TOML ║ B4 Parser in z42

0.3.7  收尾：B CI bit-identical gate 全绿 + A perf delta report
```

`║ =` 同子版本三主线并行推进。

## 依赖图

```
0.3.0 GC v1 ──► 0.3.1 三 spec ──► 0.3.2 A 包重组
                                       │
                                       ▼
                              0.3.3 A bench ║ B Lexer ║ C metadata
                                       │
                                       ▼ (A 重组完成后 B/C 才有稳定的包路径引用)
                              0.3.4–0.3.6 三主线并行
                                       │
                                       ▼
                              0.3.7 收尾 + gate

0.3.5 C3 Attribute ◄── attribute 机制 spec（0.3.4 起草）
                      ◄── C2 typeof（C 链内依赖）

0.5.x L3-G 泛型 + L3-C lambda 落地 ──► 0.5 B 剩余子系统
                                  ──► 0.5 反射完整版（Method.Invoke）
                                  ──► 1.0 byte-identical 替换
```

## 退出标准（GREEN）

- **A 主线**：22 包审计 spec 落地 + 重组完成 + 每包 bench baseline + 三轮 perf 攻坚 delta report 公开（bench-baselines branch）
- **B 主线**：Lexer / Project / Driver / Parser 4 子系统 z42 实现 CI bit-identical gate 7 日零飘移
- **C 主线**：z42.reflection 包公开 + 4 个反射对象类型 + GetMembers 系列 + typeof / GetType + Attribute reflection；MVP 单元测试覆盖率 ≥ 90%

## Open Questions（spec 起草阶段需 User 裁决）

1. **A0**：22 包审计中合并 / 拆分提案（package-by-package 裁决）
2. **A0**：Console placement 最终方案（A/B/C 三选一）
3. **B0**：bit-identical CI gate 触发频率（每 PR / 每日 / 每 commit）
4. **C0**：`Type` 对象的生命周期（cached / per-call new）+ 与 GC 的交互
5. **C3 前置**：attribute 机制 spec 范围（仅 [Test] / 通用用户定义 / 何种语法）

---

**审批**：User 2026-06-05 已批四项裁决；本 proposal 与 [roadmap.md §0.3.x](../../../roadmap.md) 同步登记。具体 A0 / B0 / C0 三 spec 在 0.3.1 启动时分别开 `add-stdlib-reorg-audit` / `add-bootstrap-easy-subsystems` / `add-reflection-mvp` 三个独立 change spec。

---

## B 主线深度规划（编译器自举）

User 2026-06-05 第二轮裁决专门细化 B 主线。决议四点：

| 决策点 | 裁决 |
|------|------|
| z42 编译器源码目录 | `src/libraries/z42.compiler/`（与 stdlib package 一致布局；ship 为 zpkg 与 `project_mobile_no_compiler` memory 一致）|
| 0.3.x 阶段 build 命令的桥接策略 | **无桥接**：z42 端只 ship 能独立跑的命令；`build` 在 0.3.x 报 "not implemented"；等 0.5+ 全子系统完成后整体替换 |
| compile-perf bench corpus | 单文件 / 文件集 end-to-end 编译时间对比；**0.5.x 全子系统就绪后才启用**；0.3.x 只跑 per-subsystem micro-bench |
| Perf gate 阈值 | median ≤ 3× · P99 ≤ 5× · 回归 > 15% 红线 |

### 1. 目录布局

```
src/libraries/z42.compiler/
├── z42.compiler.toml         # [project].name = "z42.compiler"
├── README.md
├── src/
│   ├── syntax/               # Lexer + Parser + AST (0.3 B1+B4)
│   ├── project/              # ProjectManifest + workspace (0.3 B2)
│   ├── driver/               # CLI + 错误码格式化 (0.3 B3)
│   ├── core/                 # 共享基础类型
│   ├── semantics/            # 占位（0.5 启动；L3-G/C 后）
│   ├── ir/                   # 占位（0.5+）
│   └── pipeline/             # 占位（0.5+）
└── tests/
    ├── lexer_tests.z42       # z42 端单元测试
    ├── parser_tests.z42
    ├── project_tests.z42
    ├── driver_tests.z42
    └── bit_identical/        # 与 C# 实现产物的对账 harness
        ├── lexer_oracle.z42  # 跑 z42 Lexer + dotnet z42c lex → 逐字节比对
        └── ...
```

**产物**：单一 `z42.compiler.zpkg`（与其他 stdlib package 同等地位，由 `xtask build stdlib` 一并构建）。

**1.0 byte-identical 切换路径**：

```bash
# 当 1.0 替换 gate 全部满足时：
git mv src/compiler/ src/compiler-bootstrap-archive/
# z42.compiler.zpkg 成为 ship 形态的唯一编译器；launcher 内置调用它
# src/compiler-bootstrap-archive/ 保留一段时间用于 byte-identical regression 回归
```

### 2. CLI parity（无桥接策略）

z42c-selfhost 通过 launcher 调用：

```bash
# 0.3.x 内支持（独立 z42 实现，无 dotnet 依赖）
.z42/z42 artifacts/libraries/dist/release/z42.compiler.zpkg -- lex foo.z42
.z42/z42 artifacts/libraries/dist/release/z42.compiler.zpkg -- parse foo.z42
.z42/z42 artifacts/libraries/dist/release/z42.compiler.zpkg -- manifest-check z42.toml

# 0.3.x 内**不**支持（用户调用时报 "not implemented"）
.z42/z42 ... z42.compiler.zpkg -- build src/libraries/z42.io/
.z42/z42 ... z42.compiler.zpkg -- disasm foo.zbc

# build / 完整管线在 0.5+ 全子系统完成后才启用
```

**关键**：z42 端不调用 dotnet z42c.dll 作为 fallback。两实现完全独立，对 PR 干扰为零；0.3.x 期间 default 编译器仍是 C# 实现。

### 3. REPL（两阶段）

#### Phase 2a — 0.3.x：CLI parity only（**无 REPL**）

仅 `lex` / `parse` / `manifest-check` 子命令；交互式 REPL 不在 0.3.x 范围（缺 Semantic + TypeChecker，无法 eval）。

#### Phase 2b — 0.5.x：完整 REPL（capstone deliverable）

```bash
.z42/z42 repl
> var x = 42
> x * 2
84
> class Point { int X; int Y; }
> new Point { X = 1, Y = 2 }
Point { X = 1, Y = 2 }
```

依赖：

- Semantic + TypeChecker + IR + 持久化符号表（0.5 B 剩余子系统完成）
- interp eval-loop（已有；REPL 复用）
- 跨 line scope 设计（0.5 spec 时裁决）

作为 0.5.x B 完成态的 capstone deliverable，单独 spec `add-z42-repl`。

### 4. Perf 1/3 of C#（量化定义）

**最终目标**（1.0 切换前置）：z42c-selfhost 在 z42-JIT 下编译同一 corpus 的 wall time ≤ **3× dotnet z42c.dll** 在 .NET JIT 下的 wall time。

#### 0.3.x：per-subsystem micro-bench（**端到端 compile-perf 不在 0.3.x**）

```
bench/scenarios/selfhost-micro/
├── lexer_throughput.z42      # 跑 N MB z42 源 → token stream；记录 MB/s
├── parser_throughput.z42     # 跑同样源 → AST；记录 nodes/s
├── manifest_parse.z42        # 100 个 z42.toml → 解析结果；记录 ops/s
└── README.md
```

每 micro-bench 同时跑 C# 实现（dotnet）和 z42 实现（launcher），输出 ratio。**0.3.x 不设硬阈值**，仅记录 baseline 在 `bench-baselines` 分支；后续 PR 跟踪趋势。

#### 0.5.x：end-to-end compile-perf bench + CI gate

```
bench/scenarios/compile-perf/
├── corpus/                   # 代表性 z42 源文件集（具体规模 0.5 spec 时定）
│   └── ...
├── csharp.sh                 # dotnet z42c.dll build corpus → wall time
├── selfhost.sh               # .z42/z42 z42.compiler.zpkg -- build corpus → wall time
└── README.md
```

`.github/workflows/compile-perf.yml`：

- 触发：PR 修改 `src/libraries/z42.compiler/**` OR `src/runtime/src/jit/**` OR `src/runtime/src/interp/**`
- 跑 compile-perf bench → baseline diff
- **阈值（硬性）**：
  - median ratio (z42 / C#) **≤ 3.0** → 超出红线 PR 红
  - P99 ratio **≤ 5.0** → 超出黄线 warning
  - **回归 > 15%**（与上次 baseline 比）→ 红线
- baseline 存 `bench-baselines` 分支

#### 渐进达标节奏

| 阶段 | end-to-end ratio (z42 / C#) | 触发节点 |
|------|:------:|------|
| 0.3 B1 (Lexer only) | — | 仅 Lexer micro-bench；无端到端 |
| 0.3 B4 (含 Parser) | — | 仅 micro-bench；无端到端 |
| 0.5 B 全子系统 ready | ≤ 5× | end-to-end bench 启用，阈值放松起步 |
| 0.5+ 优化迭代 | ≤ 3× | byte-identical 切换前置硬条件 |

### 5. 1.0 byte-identical 替换 gate（硬性条件）

C# → z42 整体切换必须**全部满足**：

1. ✅ 全 9 子系统在 z42 中完成（含 Sem/TC/IR/Pipeline）
2. ✅ stdlib + bench corpus + 自举编译器源码自身，全部产生**字节相同**的 .zbc / .zpkg
3. ✅ compile-perf median ratio ≤ 3.0（连续 7 日零回归）+ P99 ≤ 5.0
4. ✅ 现有全部 dotnet test / xtask test 在 z42c-selfhost 下全绿
5. ✅ 1 月 nightly opt-in soak（`Z42_COMPILER=selfhost` 环境变量）零 P0 报告

满足 → 删除 `src/compiler/` + z42.compiler.zpkg 成为唯一 ship 形态 → 1.0 byte-identical 自举完成。

### 6. dogfooding 反馈循环（贯穿 0.3 → 1.0）

写 z42 编译器期间发现的所有 gap 进入对应主线：

| 发现类型 | 处理方式 |
|--------|--------|
| 缺 stdlib API（z42.text 缺方法 / z42.io 缺类型）| → 进 A 主线 0.3.A1/A2 范围；**禁止 workaround**（per `feedback_dogfood_fill_gaps`）|
| 语言机制 friction（visitor pattern / 集合 / dispatch）| 若 L1/L2 可解 → 当次 spec；若需 L3 → 进 features.md Deferred + 推迟该子系统到 0.5.x |
| 性能 hotspot（VM 解释器 / GC / JIT）| → 进 perf bench；触发 runtime spec |
| 错误处理 / 异常路径不够用 | → A 主线 0.3.A 范围；增补 Exception 子类 / Result（L3 时） |
