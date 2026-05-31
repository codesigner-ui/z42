# Tasks: [Benchmark] Bencher-arg via AST desugar

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：lang (attribute desugar)

## 进度概览

- [x] 阶段 1: `BenchmarkDesugar` 新 pass
- [x] 阶段 2: PipelineCore 注入 (CheckAndGenerate + CheckOnly)
- [x] 阶段 3: 单元测试 BenchmarkDesugarTests
- [x] 阶段 4: E2E demo (bench_demo.z42 加 Bencher-arg 用例)
- [x] 阶段 5: 文档 (用法 + 设计思路 + Bencher.z42 / README / validator docstring)
- [x] 阶段 6: GREEN + commit + archive

## 阶段 1: BenchmarkDesugar

- [x] 1.1 NEW `src/compiler/z42.Semantics/Codegen/BenchmarkDesugar.cs`
  - `public static CompilationUnit Run(CompilationUnit cu)`
  - `IsBencherArgBenchmark(FunctionDecl)`: has `[Benchmark]` attr AND
    Params.Count==1 AND Params[0].Type is NamedType{Name:"Bencher"}
  - `Demote(fn)`: `fn with { Name = fn.Name+"$impl", TestAttributes = null }`
  - `SynthesizeWrapper(fn)`: zero-arg FunctionDecl named `fn.Name`,
    TestAttributes = fn.TestAttributes, body = [VarDecl new Bencher,
    ExprStmt call $impl(b), ExprStmt b.printSummary(name)]
  - No-op fast path (no match → return cu unchanged)
  - All synthesized nodes use `default(Span)`
- [x] 1.2 Helper to build the 3 body statements (design.md Implementation Notes)

## 阶段 2: Pipeline 注入

- [x] 2.1 `PipelineCore.CheckAndGenerate` (private impl, ~line 109): insert
  `cu = BenchmarkDesugar.Run(cu);` before `new TypeChecker(...).Check(cu, ...)`
- [x] 2.2 `PipelineCore.CheckOnly` (~line 91): same insertion before TypeChecker
- [x] 2.3 `using Z42.Semantics.Codegen;` already imported (IrGen) — confirm

## 阶段 3: 单元测试

- [x] 3.1 NEW `src/compiler/z42.Tests/BenchmarkDesugarTests.cs` — parse a CU,
  run `BenchmarkDesugar.Run`, assert function list (spec Testing Strategy
  table 9 cases): desugar pair / attr migration / body shape / label=name /
  zero-arg untouched / non-Bencher untouched / [Test] untouched / no-op /
  impl body preserved
- [x] 3.2 helper `ParseCu(src)` (mirror TestAttributeTests)
- [x] 3.3 `dotnet test --filter BenchmarkDesugar` GREEN

## 阶段 4: E2E demo

- [x] 4.1 `src/libraries/z42.test/tests/bench_demo.z42` 加:
  ```
  [Benchmark]
  void bench_add_argform(Bencher b) {
      b.iter(() => { int x = 1 + 2 + 3; BenchHelpers.blackBox(x); });
  }
  ```
- [x] 4.2 `./scripts/build-stdlib.sh` + `./scripts/test-stdlib.sh z42.test` GREEN
- [x] 4.3 spot-check `cargo run -- <bench_demo.zbc> --format json`:
  `bench_add_argform` 出现, status passed, bench_stats.label=="bench_add_argform"

## 阶段 5: 文档

### 5.A 用法
- [x] 5.A.1 `docs/design/testing/testing.md` Benchmark section: 两种签名
  (`void f()` / `void f(Bencher b)`) + desugar 说明 + label 派生 + top-level-only 限制
- [x] 5.A.2 `src/libraries/z42.test/src/Bencher.z42` docstring: 两形态都支持
- [x] 5.A.3 `src/libraries/z42.test/README.md` 能力表: Bencher-arg ✅

### 5.B 设计思路
- [x] 5.B.1 `docs/design/testing/testing.md` 同节: AST-desugar 为何优于
  runtime-ObjNew / compiler-IR-synthesis (引 design.md Decision 1) +
  naming/$impl + validator-unchanged 关键性质
- [x] 5.B.2 `docs/design/compiler/compiler-architecture.md` (若存在 desugar/
  lowering 段): 登记 BenchmarkDesugar pass 在 pre-typecheck 的位置 + 原理
  (实现原理文档规则)
- [x] 5.B.3 `TestAttributeValidator.ValidateBenchmarkFullSignature` docstring:
  注明 Bencher-arg 经 desugar 接受; validator 仍只见 zero-arg

### 5.C Deferred 清理
- [x] 5.C.1 `docs/design/testing/testing.md` Deferred 表删
  `bench-bencher-arg-trampoline` 行 (本 spec 落地)

## 阶段 6: GREEN + commit + archive

- [x] 6.1 `dotnet build src/compiler/z42.slnx` 通过
- [x] 6.2 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿 (含新 desugar tests + 现有 Benchmark validator tests 不回归)
- [x] 6.3 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 6.4 commit + push
- [x] 6.5 归档 `docs/spec/changes/add-benchmark-bencher-arg-trampoline/` →
  `docs/spec/archive/2026-05-31-add-benchmark-bencher-arg-trampoline/`
- [x] 6.6 push 归档

## 备注

- 验证器**无需改逻辑** — desugar 在 validate 前把 Bencher-arg 转成 zero-arg
- `$` 非法标识符字符 → `f$impl` collision-proof
- 单 chokepoint CheckAndGenerate (+CheckOnly) 覆盖 single-file + package 两路
- 风险: user 同时有 `[Benchmark] f(Bencher)` + 真 `f()` → E0408 重复定义
  (rare, 自然报错); class-method benchmark 不处理 (top-level only, documented)
