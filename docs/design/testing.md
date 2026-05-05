# z42 测试框架（Testing）

## 设计目标

参 Rust libtest / Go testing / xUnit 的成熟模式，让 z42 提供：

1. **编译时测试发现**：编译器扫描 `[Test]` 等 attribute → zbc 中的 `TIDX` section；运行时**不**扫整个 method table
2. **结构化 assertion**：`Assert.eq` / `Assert.throws<E>` / `Assert.near` 等替代 stdout 字面量比对
3. **条件跳过**：`[Skip(platform: "ios", feature: "jit", ...)]` 按平台 / 特性自动决定是否运行
4. **多层组织**：单元 / stdlib API / VM × 脚本工程级集成 各自归位
5. **跨平台一致**：同一份 `.zbc` 在 desktop / wasm / iOS / Android 跑相同结果（P4 范围）

## 架构总览（R 系列）

```
   .z42 源码                   C# 编译器                     zbc 二进制
   ─────────                   ─────────                     ───────────
   [Test]                      Lex / Parse / TypeCheck      TIDX section:
   fn test_x()         ──►     AttributeBinder         ──►    [
                               收集 z42.test.* attrs           {method_id, kind=Test, ...},
   [Skip(platform:             写 IrModule.TestIndex           {method_id, kind=Setup, ...},
     "ios", reason: ...)]      [R4: 校验签名]                  ...
                                                              ]
                            ────────────────────────────────────────────────
                            ▼

   Rust 端 (R1)
       LoadedArtifact.test_index                       z42.test 库 (R2)
        ↑ load_artifact 读 TIDX                       ↳ Test/Skip/Setup/...
        ↓                                              ↳ Assert.* / TestIO.*
                                                       ↳ Bencher.iter
   z42-test-runner (R3) ────────────────────────────►
       ↳ 遍历 test_index
       ↳ Setup → 测试体 → Teardown
       ↳ catch TestFailure / SkipSignal
       ↳ TAP / JSON / pretty 输出
```

R1 已落地 (commits ea54554 / bb2df98 / 5180d21)：编译时发现 + 6 个 attribute (`[Test]` / `[Benchmark]` / `[Setup]` / `[Teardown]` / `[Ignore]` / `[Skip]`) + zbc TIDX v=2 section。

**待落地**：

- **R2** — z42.test 库扩展（Assert API + TestIO.captureStdout + Bencher.iter + native helpers）
- **R3** — z42-test-runner 工具（TAP/JSON/pretty 输出 + Setup-Teardown 调度 + Bencher 执行）
- **R4** — 编译期 attribute 校验（Z0911-Z0915）含 `[ShouldThrow<E>]` + `[TestCase(args)]`
- **R5** — stdlib 各库 `tests/` 补本地原生测试（不大规模迁移现有 golden）

---

## 测试目录组织（约定）

z42 项目的测试**分四类**，各自在源码树中有固定位置：

```
src/
├── compiler/z42.Tests/              # 编译器单元测试 (xUnit)
├── runtime/tests/                   # VM 端到端 (cargo test) — golden + zbc_compat + cross-zpkg
├── libraries/
│   ├── z42.core/{src/, tests/}      # 各 stdlib 库的本地 API 测试 ([Test] + Assert)
│   ├── z42.text/{src/, tests/}
│   └── ...
└── tests/                           # VM × 脚本工程级集成（GC 压测、执行模式 parity 等）
    ├── README.md
    ├── gc-stress/                   # 形态自决：可以是 Rust crate
    │   ├── Cargo.toml               #   ↑ 加入 src/runtime/Cargo.toml workspace.members
    │   ├── run.sh                   #   入口约定
    │   ├── src/main.rs
    │   └── scenarios/*.z42
    └── workspace-build/             # 也可以是纯脚本（无 Rust 依赖）
        ├── README.md
        ├── run.sh                   #   bash 调 dotnet / z42c / z42vm
        └── ...
```

### 各层职责

| 目录 | 形态 | 谁运行 | 用例风格 |
|------|------|-------|---------|
| `src/compiler/z42.Tests/` | C# xUnit project (`.csproj`) | `dotnet test` | 编译器单元测试 (Lexer/Parser/TypeCheck/IRGen) |
| `src/runtime/tests/` | Rust integration tests (`*.rs`) + golden 用例 | `cargo test` + `./scripts/test-vm.sh` | VM 端到端 (golden) + 跨语言契约 (zbc_compat) + 多 zpkg 链接 (cross-zpkg) |
| `src/libraries/<lib>/tests/` | `.z42` 文件，含 `[Test]` 注解 | z42-test-runner (R3) | stdlib 公共 API 行为校验 |
| `src/tests/<case>/` | 异构（Rust crate 或纯脚本） | `<case>/run.sh`（统一入口约定） | 跨组件工程级集成（GC 压测、执行模式对比、工程系统验收等） |

### `src/tests/` 用例约定

每个 `<case>/` 必须有可执行的 `run.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail
# Rust 后端用例：通过 cargo 间接执行
cargo run -p gc-stress --release -- --iterations 100

# 纯脚本用例（无 Rust）：直接调 dotnet / z42c / z42vm
# dotnet run --project ../../compiler/z42.Driver -- check workspace.toml
```

退出码：`0` = 通过；非零 = 失败。

Rust 后端用例的 `Cargo.toml` 加入 `src/runtime/Cargo.toml` 的 `[workspace] members`，与 z42-runtime 共享依赖图。

### Golden 用例归属（2026-04-29 微调）

R5 期间原计划保留所有 golden 在 `src/runtime/tests/golden/run/`，归档时改为**按归属轻度拆分**：

- **VM-only**（不依赖 stdlib 的 lexer / parser / typecheck / interp / JIT 用例）：留在 `src/runtime/tests/golden/run/<NN>_<name>/`
- **Stdlib-bound**（用例的核心是验证某个 stdlib 库的行为）：迁到 `src/libraries/<lib>/tests/golden/<NN>_<name>/`，与该库源码同居
  - z42.collections — `List<T>` / `Dictionary<K,V>` / `Stack<T>` / `Queue<T>` / `LinkedList<T>` 相关 8 例
  - z42.math — `Math` / `Random` 相关 3 例
  - z42.text — `StringBuilder` 1 例
  - z42.test — runner / assert dogfood 1 例（`18_test_runner`）

`scripts/test-vm.sh` 与 `scripts/regen-golden-tests.sh` 通过 `GOLDEN_GLOBS` 同时扫两个根，**对 CI 不增加配置**，对开发者却让"改某个库时哪些 golden 受影响"变得直接可见。

格式与 stdout-比对的运行机制不变；与 `tests/*.z42`（`[Test]` + Assert 单元测试）共存于同一 `tests/` 目录树。

---

## R4.B Generic Attribute Syntax `[Name<TypeArg>]`（2026-04-30）

R4.A 落地了 6 个 z42.test attribute 的解析与签名校验；R4.B 增补**单类型参数泛型 attribute**语法，唯一即时用例是 `[ShouldThrow<E>]`（z42.test 库自检"应抛 E 类型异常"的负向路径）。

### 语法

```z42
[Test]
[ShouldThrow<TestFailure>]
void test_assert_fail_throws() {
    Assert.Fail("expected to fail");
}
```

- 单类型参数；多参 `[X<A, B>]` 和嵌套 `[X<List<int>>]` 报 parser 错（`E0202`）
- Parser 接受任意 `[Name<T>]` 写法，**哪些 attribute 允许 type arg** 由语义校验（E0913）决定
- 类型参数允许短名（`TestFailure`），与 `class X : Exception` 一致；TIDX 写源码原文，运行时按需规范化

### 编译期写入 TIDX 流程

```
parser collects [ShouldThrow<E>]
  ↓ TestAttribute.TypeArg = "E"
TestAttributeValidator (E0913 / E0914 checks)
  ├─ TypeArg 必填（不能裸 [ShouldThrow]）
  ├─ 类型必须存在于 SemanticModel.Classes
  ├─ 类型必须继承 Exception（沿 BaseClassName 链回溯）
  └─ ShouldThrow 必须配 [Test] / [Benchmark]（修饰符语义）
  ↓
IrGen 写入：TestEntry.ExpectedThrowTypeIdx = pool.intern("E") + 1
            TestFlags.ShouldThrow 位置位
  ↓ TIDX section v=2 字段已在 R1.C 预留
ZbcReader → Rust loader → resolve_test_index_strings
  → TestEntry.expected_throw_type = Some("E")
```

### Runtime 比对（A2，2026-04-30）

z42-test-runner 读 TIDX `expected_throw_type` 比对实际抛出：

- **未抛**（exit 0）→ Failed `expected to throw <E>, but no exception was thrown`
- **类型匹配**（FQ 相等 OR 短名相等，对 chain 中任一 entry 匹配即算）→ Passed
- **类型不匹配** → Failed `expected to throw <E>, got <X>`

类型提取：从 stderr 的 `Error: uncaught exception: ` 后取 `[A-Za-z0-9_.]+`，覆盖 `<TYPE>: <msg>` 与 `<TYPE>{field=...}` 两种 z42vm 输出格式。

### Inheritance 比对（A3，2026-04-30）

`[ShouldThrow<Base>]` 也匹配 Base 的子类（编译期展开方案，运行时无需类型反射）：

- **C# IrGen 端**：`BuildShouldThrowChain(typeArg)` 遍历 `SemanticModel.Classes`，把 `typeArg` + 所有从 `typeArg` 派生的类的短名拼成 `;`-delimited 字符串写入 TIDX `expected_throw_type` 槽。例如 `[ShouldThrow<Exception>]` 在 z42.test dogfood 的 CU 里展开为 `"Exception;TestFailure;SkipSignal"`。
- **Runner 端**：split `expected_throw_type` 后任一 entry 命中即 Pass；同样的 `type_matches`（FQ vs 短名）规则
- **覆盖范围**：仅当前 CU 的 `SemanticModel.Classes` 可见类（含 `using` 引入的 import）；不在 import 链路上的 zpkg 依赖不会枚举（这些场景下 fallback 到直接匹配）
- **零格式 bump**：TIDX layout 不变；`expected_throw_type` 字段语义从"单个类型名"扩展为"类型名或 `;`-delimited list"

### 当前不做的

- ⏸️ 跨非 import zpkg 依赖的 inheritance（要求 runner 知道完整类型层次，需做 LazyLoader 集成 → 由 R3 完整版承担）
- ⏸️ 多类型参数 `[X<A, B>]` / 嵌套 `[X<List<Y>>]` / dotted name `<Std.E>`

---

## Runner 输出格式（R3a，2026-04-30）

`z42-test-runner --format <pretty|tap|json>` 三选一。`--filter <SUBSTR>` 按方法名 substring 筛选；多个 `--format` 等价于最后一个。

### 默认 format 选择

- TTY 上 stdout → `pretty`（人类可读，带颜色）
- 非 TTY（管道、重定向、CI）→ `tap`（机器消费默认）
- 显式 `--format X` 强制覆盖

### Pretty

R3a 保留原 R3 minimal 输出语义，仅在收集所有结果之后再渲染。和 A2/A3 阶段视觉等价。

### TAP 13 ([testanything.org](https://testanything.org/tap-version-13-specification.html))

```
TAP version 13
1..8
ok 1 - Z42TestDogfood.test_assert_equal_pass
ok 2 - Z42TestDogfood.test_skip # SKIP platform=ios
not ok 3 - Z42TestDogfood.test_fail
  ---
  message: 'expected `Foo`, got `Bar`'
  ...
```

YAML diagnostic 块仅在 `not ok` 后输出（`ok` 不带 reason）；skip 用 `# SKIP <reason>` 指令。

### JSON

自定义 schema，可扩字段（保持向后兼容）：

```json
{
  "tool": "z42-test-runner",
  "version": "0.1.0",
  "module": "z42.test_dogfood.zbc",
  "summary": {
    "total": 8, "passed": 8, "failed": 0, "skipped": 0,
    "duration_ms": 48
  },
  "tests": [
    { "name": "Z42TestDogfood.test_assert_equal_pass",
      "status": "passed", "duration_ms": 6 },
    { "name": "Z42TestDogfood.test_skip",
      "status": "skipped", "duration_ms": 0,
      "reason": "platform=ios" },
    { "name": "Z42TestDogfood.test_fail",
      "status": "failed", "duration_ms": 7,
      "reason": "expected `Foo`, got `Bar`" }
  ]
}
```

`reason` 字段对 `passed` 测试不输出（serde `skip_serializing_if`）。后续 R3b 加 `setup_duration_ms` / `teardown_duration_ms` 时不破坏现有消费者。

### --filter

substring match 而非 regex；不引入 `regex` 依赖。`test.method_name.contains(filter)` 为 true 即保留。如未来需 regex，独立 spec 升级。

### 退出码

- `0` — 全部通过 / 仅 skipped
- `1` — 任一 failed
- `2` — runner 内部错误（路径解析、I/O 等）
- `3` — 0 tests discovered（含被 filter 排空）

---

## 增量测试 `just test-changed`（R3c，2026-04-30）

[scripts/test-changed.sh](../../scripts/test-changed.sh) 把 `git diff` 的变更文件映射到受影响测试集合，跳过无关测试加快本地反馈。

### Base ref 解析

按优先级：

1. 环境变量 `Z42_TEST_CHANGED_BASE`（CI 友好，例如 `origin/main`）
2. 命令行第一参数（`just test-changed main`）
3. 默认 `HEAD`（工作区 + staged 修改）

untracked 文件（`git ls-files --others --exclude-standard`）也纳入变更集。

### 文件 → 测试映射

| 变更路径 | 触发命令 |
|---------|---------|
| `src/libraries/<lib>/src/**` | `just test-stdlib <lib>` + `just test-vm` |
| `src/libraries/<lib>/tests/**` | `just test-stdlib <lib>` |
| `src/libraries/<lib>/<lib>.toml` | `just test-stdlib <lib>` |
| `src/runtime/src/**`、`src/runtime/Cargo.toml` | `cargo test runtime` + `just test-vm` |
| `src/runtime/tests/cross-zpkg/**` | `just test-cross-zpkg` |
| `src/runtime/tests/**`（其他） | `just test-vm` |
| `src/compiler/**` | `just test-compiler` + `just test-vm` |
| `src/toolchain/**` | `cargo test test-runner` + `just test-stdlib` |
| `scripts/test-vm.sh`、`scripts/regen-golden-tests.sh` | `just test-vm` |
| `scripts/test-stdlib.sh`、`scripts/build-stdlib.sh` | `just test-stdlib` |
| `scripts/test-cross-zpkg.sh` | `just test-cross-zpkg` |
| `justfile`、`*.workspace.toml`、`src/runtime/build.rs` | 全套 `just test` |
| `*.md`、`docs/**`、`spec/**`、`.claude/**`、`README*` | 不触发 |
| 其他 `src/**` 或未识别的根级文件 | 全套 `just test`（防御性） |

去重后按"先编译后 VM 后 stdlib 后 cross"的隐式顺序串行执行；任一命令失败即停（透传退出码）。

### 限制（R3c 范围）

- 目录级粗粒度映射；不读 IR / 类级反向依赖图（需独立 spec）
- 不缓存上次结果（每次重新跑选中的命令）
- 单 base ref；不支持区间或多 ref
- 不监听文件变更（无 watch 模式）

### 用例

```bash
# 改了 z42.math 一个文件 → 只跑 z42.math + VM goldens
just test-changed

# PR 检查：只跑 main 与当前 branch 之间的差异影响范围
Z42_TEST_CHANGED_BASE=origin/main just test-changed

# 看计划不执行（pre-commit hook 友好）
just test-changed --dry-run
```
- ⏸️ TypeArg 升级为 TypeExpr（当前 `string?` 足够）
- ⏸️ user-defined attributes（z42 当前白名单：z42.test.* + Native 两个 family）

### 错误码

- **E0913** `ShouldThrowTypeInvalid`：3 种触发（缺 type arg / 类型不存在 / 不继承 Exception）+ 1 种"非 ShouldThrow attribute 上有 type arg"
- **E0914** `SkipReasonMissing`（沿用）：扩展为 `[Skip] / [Ignore] / [ShouldThrow]` 三者任一缺 [Test]/[Benchmark] 都报错

---

## TestIO（R2 完整版，2026-05-05）

`Std.Test.TestIO` 三个静态方法，让测试代码捕获被测代码的 console 输出。lambda + delegate 已落地后兑现。

```z42
public static class TestIO {
    public static string captureStdout(Action body);
    public static string captureStderr(Action body);
    public static CaptureResult captureBoth(Action body);
}

public class CaptureResult {
    public string Stdout { get; }
    public string Stderr { get; }
}
```

### 实现要点

- **Stack 语义**：`__test_io_install_*_sink` push 一个 `Vec<u8>`，`__test_io_take_*_buffer` pop。嵌套合法（内层 push/pop 不影响外层 buffer）。
- **异常透传**：捕获过程中 body 抛 → take_buffer 在 catch 块里调一次确保 sink pop，再 `throw e;` 重抛。每个 `captureStdout` 调用前后 sink stack 深度守恒。
- **stderr 先决条件**：z42.io 加 `Std.IO.ConsoleError` 类（`WriteLine` / `Write`，binding `__eprintln` / `__eprint`），让 z42 用户首次能写 stderr。`captureBoth` 同时 install 两个 sink；channel 之间不混合。

### 错误模式

> 写测试时记得：z42 lambda **快照捕获值类型**（[examples/closure_capture.z42](../../examples/closure_capture.z42)）。要把 capture 结果传出 lambda body 必须用引用类型（class/array），不能直接对外部 int / string 局部变量赋值。dogfood 用 `IntCell` / `StrCell` 等 wrapper class 演示这个模式。

---

## Bencher（R2 完整版，2026-05-05）

`Std.Test.Bencher` 给代码段做 wall-clock 测量。当前 runner 不调度 [Benchmark] 方法（独立 spec），用户在 [Test] 里手动 `var b = new Bencher(); b.iter(() => ...);` 即可。

```z42
public class Bencher {
    public Bencher();                              // warmup=10 / samples=100
    public Bencher(int warmupIters, int sampleIters);
    public void iter(Action body);
    public long MinNs { get; }
    public long MaxNs { get; }
    public long MedianNs { get; }
    public long TotalNs { get; }
    public int  Samples { get; }
    public void printSummary(string label);
}

public static class BenchHelpers {
    public static object blackBox(object value);   // identity；JIT 端预留 hook
}
```

### Native helpers

- `__bench_now_ns` — `OnceLock<Instant>` epoch + `Instant::now().elapsed().as_nanos()`，单调性由 `std::time::Instant` 保证
- `__bench_black_box` — interp 端 `args[0].clone()`；future JIT 端可挂钩防止 dead-code elimination

### TestAttributeValidator E0912 完整化（R2 完整版）

R4.A 留的"first-parameter-is-Bencher"检查现在补齐：`[Benchmark]` 方法必须 `void f(Bencher b)`（exactly 1 个参数；类型短名 `Bencher`）。0 / 多余参数 / 错类型都报 E0912。

### Runner [Benchmark] 调度（未做）

当前 runner `entry.kind != TestEntryKind::Test { continue; }`：[Benchmark] 函数被 discovery 过滤掉。运行 / JSON 输出 / criterion-style baseline diff 留给独立 spec。

---

## 实施期间发现的 z42 反射限制（2026-05-05）

R2 完整版实施时碰到 z42 当前的三个 runtime-type 识别 bug，记录于此供未来 compiler-fix spec 清算：

| 路径 | 现象 | 实测 |
|---|---|---|
| `e is X` cross-module（X 是导入类的短名）| 当 `e` 静态类型 = Exception、运行时 = Std.TestFailure → IsInstance 返回 false | dogfood debug 多次复现 |
| generic-E `is`（`is E` where E is type-param）| IR 端 IsInstance 接受编译期固定 class_name | spec 明文不支持 |
| `Object.GetType()` on Exception 子类 | VCall 找不到方法（vtable 跨多层继承时 Object 方法未传递）| `unknown method GetType` 报错 |

这些限制让 `Assert.Throws<E>(Action)` / `Assert.Throws(string typeName, Action)` 都不可用；本期最终 API 是 untyped 的 `Assert.Throws(Action)` —— "应抛特定类型"用 `[ShouldThrow<E>]` 测试级注解（编译期写 chain → runner 字符串匹配，绕开 reflection）。

---

## Benchmark 与 Test 分离原则

z42 **不**把 benchmark 放到 `src/tests/` 下，而是分离两个不同的目录树。这与 Rust / C++ / .NET / Java / Haskell 等主流静态语言一致。

### 当前 bench 目录布局

```
bench/                                   # 顶层（性能 + 基线 + 工具）
├── README.md
├── baseline-schema.json                 # 跨工具的 baseline JSON Schema
├── scenarios/                           # z42 端到端场景 (.z42)
│   ├── 01_fibonacci.z42
│   ├── 02_math_loop.z42
│   └── 03_startup.z42
├── baselines/                           # gitignored；CI publish 到 bench-baselines branch
└── results/                             # gitignored；当前 run 输出

src/runtime/benches/                     # Rust criterion (cargo 框架强约定位置)
├── README.md
└── smoke_bench.rs

src/compiler/z42.Bench/                  # C# BenchmarkDotNet (独立 csproj)
├── z42.Bench.csproj
├── CompileBenchmarks.cs
└── Inputs/{small,medium}.z42

scripts/
├── bench-run.sh                         # hyperfine 调度 + 写 results/e2e.json
└── bench-diff.sh                        # 比对当前与 baseline (5% 时间 / 10% 内存阈值)
```

### 为什么分离（与 `src/tests/` 不同处）

| 维度 | `src/tests/` (correctness) | `bench/` (perf) |
|------|---------------------------|-----------------|
| 入口 | `<case>/run.sh` exit 0/1 | `cargo bench` / `dotnet run -c Release` / `just bench` |
| 编译 profile | Debug 即可 | **必须 Release** |
| 输出 | 通过 / 失败 + 诊断 | metrics JSON + 置信区间 |
| 噪声容忍 | 必须确定性 | 可重跑 / warmup / 多 sample |
| CI 门禁 | hard fail | informational + 5% 阈值 |
| baseline 跟踪 | 无 | 跨 commit 持久化（独立 `bench-baselines` 分支） |

### 主流语言调研（2026-04-30）

| 语言 | tests 位置 | bench 位置 | 模式 |
|------|----------|----------|------|
| **Rust** | `tests/` | `benches/` 平行 | 分离（cargo 内置） |
| **C++** | `tests/` | `benchmarks/` | 分离（Google Benchmark 约定） |
| **.NET** | `*.Tests.csproj` | `*.Benchmarks.csproj` | 分离（BDN 必须独立 project） |
| **Java** | `src/test/java/` | `src/jmh/java/` | 分离（JMH 独立 sourceSet） |
| **Haskell** | `test/` | `bench/` | 分离（Cabal `benchmark` stanza） |
| **Python (asv)** | `tests/` | `benchmarks/` | 分离（NumPy/SciPy 用） |
| **Python (pytest-benchmark)** | `tests/test_*.py` | 同文件 fixture | **统一**（少数派） |
| **Go** | `*_test.go` | `*_test.go` 内 `BenchmarkX` | **统一**（极简哲学，唯一） |

z42 已接入 **criterion**（Rust）+ **BenchmarkDotNet**（C#）+ **hyperfine**（z42 e2e）三套独立框架，每套有自己的 baseline / diff / 报告流。"统一"模式（Go-style）建立在"唯一一套 testing 库 + 没有 baseline 概念"的前提上，z42 不具备。所以采用主流分离式。

### 架构红线

- `bench/` 顶层目录**不**移到 `src/tests/`
- `src/runtime/benches/` 与 `src/compiler/z42.Bench/` **不**移动（cargo / dotnet 框架约定）
- `src/tests/` 不放性能场景（即不出现 `perf-*` 子目录）
- `bench/scenarios/*.z42` 性能场景不混入 correctness `src/tests/<case>/`

---

## Attribute 系统（R1 已落地 6 个）

每个被 `z42.test.*` attribute 标注的函数会进入 zbc 的 TIDX section。语义校验在 R4。

| Attribute | 形式 | 语义（R3 runner 行为） |
|-----------|------|---------------------|
| `[Test]` | 无参 | 标记普通测试方法 |
| `[Benchmark]` | 无参 | 标记基准方法（runner 用不同调度） |
| `[Setup]` | 无参 | 每个 `[Test]` 前调用 |
| `[Teardown]` | 无参 | 每个 `[Test]` 后调用（即使失败） |
| `[Ignore]` | 无参 | 永久忽略（runner 不列入统计） |
| `[Skip(reason: "...", platform: "...", feature: "...")]` | 命名参数 | 跳过：reason 必填；platform 限定平台时跳过；feature 缺失时跳过 |

`[Skip]` 三个命名参数都可选（除 reason 外），可任意组合：

```z42
[Test]
[Skip(reason: "blocked by issue #123")]                                  // 总是跳
void test_known_broken() { }

[Test]
[Skip(platform: "ios", reason: "JIT not supported on iOS")]              // iOS 上跳
void test_jit_only() { }

[Test]
[Skip(feature: "multithreading", reason: "single-threaded build")]       // 缺特性时跳
void test_concurrent() { }

[Test]
[Skip(platform: "wasm", feature: "filesystem", reason: "wasm sandbox")]  // 多重条件
void test_fs_io() { }
```

R4 计划新增的 attribute（**目前 parser 不识别**）：

- `[ShouldThrow<E>]` — 期望函数抛 `E` 类型异常（需先实现 attribute 泛型语法）
- `[TestCase(args)]` — 参数化测试，可重复多次（需先实现 typed args 语法）

---

## TIDX 二进制格式（R1）

详见 [`zbc.md` 的 TIDX 段](zbc.md#tidx-test-index可选spec-r1)。

要点：
- Section tag 4 字节 ASCII：`TIDX`
- 当前版本 `v=2`（R1.C，2026-04-29）
- 仅当模块含至少一个测试 attribute 时由 `ZbcWriter.BuildTidxSection` 写入；
  缺失 = 该 .zbc 无测试
- 字符串引用为 **1-based** 索引到 `module.string_pool`，`0` 表示无值
- C# 类型：[`Z42.IR.TestEntry`](../../src/compiler/z42.IR/TestEntry.cs)
- Rust 类型：[`z42_vm::metadata::TestEntry`](../../src/runtime/src/metadata/test_index.rs)
- 跨语言契约测试：[`src/runtime/tests/zbc_compat.rs::test_demo_tidx_round_trips`](../../src/runtime/tests/zbc_compat.rs)
- 演示文件：[`examples/test_demo.z42`](../../examples/test_demo.z42)

---

## R 系列实施进度（截至 2026-04-30）

| Phase | Spec | Status | Commit |
|-------|------|--------|--------|
| R1.A+B | [add-test-metadata-section](../../spec/changes/add-test-metadata-section/) | ✅ TestEntry types + zbc TIDX v=1 plumbing | `ea54554` |
| R1.C.1 | 同上 | ✅ TIDX v=2 + skip_platform/feature fields | `bb2df98` |
| R1.C.2-5 | 同上 | ✅ parser 识别 6 attribute + IrGen + 跨语言契约 | `5180d21` |
| R1.D | 同上 | 🟡 docs（本文件 + ir.md 注 + error-codes 占位）+ archive |  |
| R2 | [extend-z42-test-library](../../spec/changes/extend-z42-test-library/) | 🔵 DRAFT | — |
| R3 | [rewrite-z42-test-runner-compile-time](../../spec/changes/rewrite-z42-test-runner-compile-time/) | 🔵 DRAFT | — |
| R4 | [compiler-validate-test-attributes](../../spec/changes/compiler-validate-test-attributes/) | 🔵 DRAFT | — |
| R5 | [rewrite-goldens-with-test-mechanism](../../spec/changes/rewrite-goldens-with-test-mechanism/) | 🔵 DRAFT (scope 缩窄) | — |

---

## 编写新测试

### 编译器层（C# xUnit）

```bash
# 加测试到 src/compiler/z42.Tests/<Topic>Tests.cs
# 运行
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
# 或
just test-compiler
```

### VM 端到端（z42 golden）

```bash
# 1. 写 src/runtime/tests/golden/run/<NN>_<name>/source.z42
# 2. 写 src/runtime/tests/golden/run/<NN>_<name>/expected_output.txt
# 3. ./scripts/regen-golden-tests.sh 编译 source.zbc
# 4. just test-vm 验证
```

### Stdlib 库本地（R3 runner 落地后）

```z42
// src/libraries/z42.text/tests/string_basics.z42
import z42.test.{Test, Assert};
import z42.text.StringBuilder;

[Test]
void test_append_concat() {
    let sb = StringBuilder();
    sb.append("a"); sb.append("b");
    Assert.eq(sb.build(), "ab");
}
```

R3 落地后通过 `just test-stdlib z42.text` 运行。

### 工程级集成（src/tests/）

```bash
mkdir -p src/tests/my-integration-test
cd src/tests/my-integration-test
# 选 1：Rust crate
cargo init --bin && # 加 Cargo.toml 到 src/runtime workspace.members
# 选 2：纯脚本
cat > run.sh <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
# ... your test logic
EOF
chmod +x run.sh
```

---

## 全绿（GREEN）标准

任何迭代进归档前，以下命令全过：

```bash
just build           # dotnet + cargo 全部编译通过
just test            # compiler + VM + cross-zpkg 全过
cargo test           # Rust 单测（含 metadata::test_index 12 个）
```

详见 [.claude/rules/workflow.md](../../.claude/rules/workflow.md) 阶段 8。
