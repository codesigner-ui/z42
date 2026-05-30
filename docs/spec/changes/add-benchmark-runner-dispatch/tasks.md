# Tasks: [Benchmark] runner dispatch (minimal v1)

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：lang (signature contract) + stdlib + test-runner

## 进度概览

- [x] 阶段 1: Validator — [Benchmark] signature → zero-arg
- [x] 阶段 2: Runner discovery + execution — include Benchmark kind
- [x] 阶段 3: TestResult.is_benchmark + pretty formatter label
- [x] 阶段 4: Bencher.z42 docstring + bench_demo.z42 E2E
- [x] 阶段 5: Tests — C# validator + Rust runner_tests + stdlib demo
- [x] 阶段 6: 文档（用法 + 设计思路）
- [x] 阶段 7: GREEN + commit + archive

## 阶段 1: Validator

- [x] 1.1 `src/compiler/z42.Semantics/TestAttributeValidator.cs::ValidateBenchmarkFullSignature`
  - 替换 "exactly 1 Bencher param" 检查为 "must take 0 params"
  - 复用 `ValidateNoArgVoidSignature(fn, "[Benchmark]", DiagnosticCodes.BenchmarkSignatureInvalid, diags)` (same shape as [Test])
  - 删除 `firstParamTypeName != "Bencher"` 分支
- [x] 1.2 `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` — E0912 description: "Benchmark methods must take zero parameters (matching [Test] shape)" + 例 "[Benchmark] void bench_foo() { var b = new Bencher(); b.iter(…); }"

## 阶段 2: Runner discovery + execution

- [x] 2.1 `src/toolchain/test-runner/src/discover.rs:49` — `entry.kind != TestEntryKind::Test` → `!matches!(entry.kind, TestEntryKind::Test | TestEntryKind::Benchmark)`; carry `is_benchmark: entry.kind == TestEntryKind::Benchmark` into DiscoveredTest
- [x] 2.2 `DiscoveredTest` 加 `pub is_benchmark: bool` 字段
- [x] 2.3 `src/toolchain/test-runner/src/main.rs:180` — 同样 filter relaxation；`DiscoveredTestOwned` 加 `is_benchmark` 字段
- [x] 2.4 Construction site at main.rs DiscoveredTest borrow → carry is_benchmark

## 阶段 3: TestResult / formatters

- [x] 3.1 `src/toolchain/test-runner/src/result.rs::TestResult` 加 `#[serde(skip_serializing_if = "is_false")] pub is_benchmark: bool` (default `false`)
- [x] 3.2 `TestResult::from_outcome` 签名加 `is_benchmark: bool` 透传; 所有 4 个 from_outcome 调用点更新
- [x] 3.3 `src/toolchain/test-runner/src/format/pretty.rs` — Passed/Failed/Skipped branches prefix `bench:` when `r.is_benchmark` (e.g. `✓ bench:bench_foo  (12ms)`)
- [x] 3.4 `result.rs` 测试更新: sample_results 加 1 个 is_benchmark=true sample, JSON serialization 测试断言 field 仅在 true 时出现

## 阶段 4: Bencher.z42 doc + E2E demo

- [x] 4.1 `src/libraries/z42.test/src/Bencher.z42` lines 13-16 删除 "future spec; for now use inside [Test]"; 改为 "`[Benchmark] void name() { var b = new Bencher(); b.iter(…); b.printSummary(\"name\") }`"
- [x] 4.2 NEW `src/libraries/z42.test/tests/bench_demo.z42`:
  - 1 个 `[Benchmark] void bench_addition()` body 构造 Bencher, iter, printSummary
  - 1 个 `[Test] void test_bencher_inside_test_still_works()` 同上 — regression: 用 Bencher inside [Test] 写法仍可用
- [x] 4.3 `./scripts/test-stdlib.sh z42.test` 通过

## 阶段 5: 测试

- [x] 5.1 `src/compiler/z42.Tests/TestAttributeTests.cs` 加 / 改:
  - `Validate_BenchmarkZeroArg_Passes`（新）
  - `Validate_BenchmarkWithBencherParam_FailsE0912`（旧通过测试需 invert）
  - `Validate_BenchmarkWithExtraParams_FailsE0912`（新；防御）
  - `Validate_BenchmarkReturnsNonVoid_FailsE0912`（保留如已有）
- [x] 5.2 (no separate Rust runner_tests needed — discovery + dispatch is mechanical pass-through; covered by e2e demo)

## 阶段 6: 文档

### 6.A 用法

- [x] 6.A.1 `docs/design/testing/testing.md` 新增 § "Benchmark runner dispatch":
  - 用法示例（zero-arg signature + Bencher inside body）
  - 输出样本 (pretty: `✓ bench:bench_foo (12ms)`; TAP: 同 Test; JSON: `is_benchmark: true`)
  - Skip / Timeout 与 Benchmark 组合: `[Benchmark] [Skip(platform: "wasm")]` 在 wasm 上跳过, 等
- [x] 6.A.2 `src/libraries/z42.test/README.md`:
  - 能力表 "Runner [Benchmark] 调度" 行: `📋 待开 spec` → `✅ add-benchmark-runner-dispatch (zero-arg form)`
  - 新增能力 "Bencher 用法" 行: `[Benchmark] void f()` + 内置 Bencher

### 6.B 设计思路

- [x] 6.B.1 `docs/design/testing/testing.md` 同节 § "Why zero-arg signature":
  - Bencher-arg form (`void f(Bencher b)`) 需要 runner-side ObjNew API 或 compiler-side trampoline; v1 跳过
  - 零迁移成本 (zero existing `[Benchmark]` users); 公开 API change clean
  - User pattern 仍可用 Bencher (just construct inside body)
  - Future spec `add-benchmark-bencher-arg-trampoline` 在 trampoline 基础设施落地后引入 Bencher-arg form

## 阶段 7: GREEN + commit + archive

- [x] 7.1 `dotnet build src/compiler/z42.slnx` 通过
- [x] 7.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（含新 Benchmark validator tests）
- [x] 7.3 `cargo build --manifest-path src/toolchain/test-runner/Cargo.toml --release` 通过
- [x] 7.4 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 7.5 commit + push
- [x] 7.6 归档 `docs/spec/changes/add-benchmark-runner-dispatch/` →
  `docs/spec/archive/2026-05-31-add-benchmark-runner-dispatch/`
- [x] 7.7 push 归档

## 备注

- Benchmark signature **change** is technically a public-API break, but
  zero existing users (grep confirmed) → no migration cost; not introducing
  versioned alias
- z42.test stdlib build 需要 regen (Bencher.z42 doc-comment 改动 = 字节
  shift 但不影响行为)
- Runner discovery filter 改动是 1-token (`!=` → `!matches!`)；signature
  contract is the meaningful 用户-visible change
