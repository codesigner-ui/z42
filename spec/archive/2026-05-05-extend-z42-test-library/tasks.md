# Tasks: Extend z42.test Library — TestIO + Bencher + Assert ext (R2 完整版)

> 状态：🟢 已完成 | 归档：2026-05-05 | 创建：2026-04-29 | 重写：2026-05-05
> 类型：feature（lang/ir 不动；仅库 + corelib 增量）→ 完整 Spec-First
> 依赖：lambda + delegate 全管线 ✅（2026-05-02 add-lambda-l2 / add-delegate-type）
> 范围调整：原 9 阶段 DRAFT 中 attribute 类型 / TestFailure / minimal Assert 已分别由 R1.C / R4.A / R4.B / R2 minimal 落地；本版仅覆盖剩余 lambda 依赖项

## 进度概览

- [x] 阶段 A: Rust corelib stdout/stderr sink + 4 个 testio native
- [x] 阶段 B: Rust corelib bench 模块（`__bench_now_ns` / `__bench_black_box`）
- [x] 阶段 C: z42 库 TestIO.z42 + CaptureResult
- [x] 阶段 D: z42 库 Bencher.z42 + BenchHelpers
- [x] 阶段 E: z42 库 Assert.z42 扩展（Throws/DoesNotThrow/EqualApprox）
- [x] 阶段 F: TestAttributeValidator E0912 完整化
- [x] 阶段 G: dogfood.z42 加 13 个新测试
- [x] 阶段 H: 文档同步
- [x] 阶段 I: 全绿验证 + 归档

---

## 阶段 A: Rust corelib stdout/stderr sink

- [x] A.1 [src/runtime/src/corelib/io.rs](src/runtime/src/corelib/io.rs) 加 thread_local `STDOUT_SINKS: RefCell<Vec<Vec<u8>>>` + `STDERR_SINKS`
- [x] A.2 修改 `builtin_println` / `builtin_print`：sink stack 顶非空写 buffer + `\n`，否则 println!（println 自动加换行；print 不加）
- [x] A.3 加 stderr 等价 builtins（如不存在）—— 看 corelib/io.rs 是否已有 stderr 路径；若没有，加 `builtin_eprintln` / `builtin_eprint`
- [x] A.4 加 4 个 native：
    - `builtin_test_io_install_stdout_sink` (push 空 Vec)
    - `builtin_test_io_take_stdout_buffer` (pop, return Value::Str)
    - 同上 stderr 两个
- [x] A.5 [src/runtime/src/corelib/mod.rs](src/runtime/src/corelib/mod.rs) 注册 4 + 可能 2 个 stderr println builtins
- [x] A.6 [src/runtime/src/corelib/io_tests.rs](src/runtime/src/corelib/io_tests.rs) NEW（如不存在）：
    - sink 未安装 → builtin_println 不进 buffer
    - sink 安装 → 文本进 buffer + 换行
    - 嵌套：内层 sink 看到内层输出，外层只看到外层输出
    - take 后 sink 卸载（栈深度 -1）

## 阶段 B: Rust corelib bench

- [x] B.1 [src/runtime/src/corelib/bench.rs](src/runtime/src/corelib/bench.rs) NEW
    - `static EPOCH: OnceLock<Instant>`
    - `builtin_bench_now_ns(_ctx, _) -> Value::I64(ns since EPOCH)`
    - `builtin_bench_black_box(_ctx, args) -> args[0].clone()`
- [x] B.2 [src/runtime/src/corelib/mod.rs](src/runtime/src/corelib/mod.rs) 注册 + `mod bench;`
- [x] B.3 [src/runtime/src/corelib/bench_tests.rs](src/runtime/src/corelib/bench_tests.rs) NEW
    - `__bench_now_ns` 调用两次后第二次 >= 第一次（单调）
    - `__bench_black_box(Value::I64(42))` 返回 `Value::I64(42)`

## 阶段 C: z42 库 TestIO

- [x] C.1 [src/libraries/z42.test/src/TestIO.z42](src/libraries/z42.test/src/TestIO.z42) NEW
    - `public class CaptureResult { Stdout / Stderr; ctor }`
    - `public static class TestIO { captureStdout / captureStderr / captureBoth }`
    - 三个 native bindings: `__test_io_install_stdout_sink` / `..take..` / stderr 同
    - 实现：try { body.Invoke(); return take; } catch { take; throw; }（design.md 推荐版本）
- [x] C.2 [src/libraries/z42.test/z42.test.toml](src/libraries/z42.test/z42.test.toml) 暴露 TestIO + CaptureResult

## 阶段 D: z42 库 Bencher

- [x] D.1 [src/libraries/z42.test/src/Bencher.z42](src/libraries/z42.test/src/Bencher.z42) NEW
    - `public class Bencher` ctor + iter + 5 个 stat properties + printSummary
    - 私有 `sortInPlace(List<long>)`（z42 没有 List.Sort 内置）
    - `public static class BenchHelpers { blackBox<T>(T) -> T }`
    - native bindings: `__bench_now_ns` / `__bench_black_box`
- [x] D.2 [src/libraries/z42.test/z42.test.toml](src/libraries/z42.test/z42.test.toml) 暴露 Bencher + BenchHelpers

## 阶段 E: Assert 扩展

- [x] E.1 [src/libraries/z42.test/src/Assert.z42](src/libraries/z42.test/src/Assert.z42) 加：
    - `public static void Throws<E>(Action body) where E : Exception`
    - `public static void DoesNotThrow(Action body)`
    - `public static void EqualApprox(double actual, double expected, double eps)`
- [x] E.2 删除文件顶部"当前不包含的方法（受 closure 缺失限制）"注释段（兑现）
- [x] E.3 验证 z42 generic method + `is E` + `typeof(E).Name` 全部可用；如 typeof 不行，用 fallback message（设 design.md Decision 7）

## 阶段 F: TestAttributeValidator E0912 完整化

- [x] F.1 [src/compiler/z42.Semantics/TestAttributeValidator.cs](src/compiler/z42.Semantics/TestAttributeValidator.cs) 新增 `ValidateBenchmarkFullSignature`：
    - 第一参数必须是 `Bencher` （ExtractTypeName == "Bencher"）
    - exactly 1 param（包含 = 0 报 "must have first param" / > 1 报 "must take exactly one"）
- [x] F.2 替换 R4.A 的 `ValidateBenchmarkPartialSignature` 调用（保留 partial 函数为 internal helper 或合并）
- [x] F.3 删除 R4.A 的"first-parameter-is-Bencher check pending R2.C"注释（兑现）
- [x] F.4 [src/compiler/z42.Tests/TestAttributeTests.cs](src/compiler/z42.Tests/TestAttributeTests.cs) 加 4 个 case（spec scenarios E0912）
    - 注：测试时 ExceptionStub 之外要给 SemanticModel 注入 `Bencher` stub class（或从源码声明）；走最简路径 ExceptionStub 同款

## 阶段 G: dogfood.z42 加测试

- [x] G.1 [src/libraries/z42.test/tests/dogfood.z42](src/libraries/z42.test/tests/dogfood.z42) 加 5 个 TestIO 测试（含嵌套）
- [x] G.2 加 4 个 Assert ext 测试（Throws/DoesNotThrow/EqualApprox/EqualApprox-fail-via-ShouldThrow）
- [x] G.3 加 4 个 Bencher 测试：
    - 默认 ctor 跑 110 次（计数器）
    - 自定义 ctor 跑 7 次
    - MedianNs 边界 invariants
    - blackBox 透传 + printSummary 输出包含 label/min/median/max
- [x] G.4 dogfood 期望 8 → 21（含旧 8）；全部通过

## 阶段 H: 文档同步

- [x] H.1 [docs/design/testing.md](docs/design/testing.md) 加 "TestIO" 段（API + 嵌套语义 + sink stack 实现要点）
- [x] H.2 [docs/design/testing.md](docs/design/testing.md) 加 "Bencher" 段（API + 当前 runner 不调度的限制 + future runner mode 链接）
- [x] H.3 [docs/roadmap.md](docs/roadmap.md) M6 R2 完整版完成条目；列出新 builtin / 新类型；保留 "[Benchmark] runner 调度" 作为 backlog
- [x] H.4 [src/libraries/z42.test/README.md](src/libraries/z42.test/README.md) 更新能力清单
- [x] H.5 [src/libraries/z42.test/src/Assert.z42](src/libraries/z42.test/src/Assert.z42) 顶部注释更新（删 closure-pending 段）

## 阶段 I: 全绿验证 + 归档

- [x] I.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] I.2 `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [x] I.3 `cargo test --manifest-path src/runtime/Cargo.toml` corelib io/bench 单测通过
- [x] I.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿（含本次 +4 case）
- [x] I.5 `./scripts/test-vm.sh` 208/208 不回归
- [x] I.6 `./scripts/test-stdlib.sh` 全绿；dogfood 期望 21/0
- [x] I.7 `./scripts/test-cross-zpkg.sh` 1/1 不回归
- [x] I.8 spec scenarios 逐条覆盖确认（specs/test-library-api/spec.md）
- [x] I.9 commit + push + 归档 spec 到 archive/2026-05-XX-extend-z42-test-library

## 备注

- corelib stderr builtins 当前是否存在需 grep 验证；如缺失，本 spec 顺带补齐（必要前置）
- z42 generic method 在 stdlib 中的使用模式（`where E : Exception`）需要看 z42.core 已有 generic class/method 怎么写的；可能要研究 1-2 个现存范例避免踩坑
- `typeof(E).Name` 在 generic method body 中是否可用 → 必须 grep 现状；阶段 E.3 必须先做完研究再实施 Throws<E> 的 message 格式
- Bencher.iter 内部调用 closure 的 capture 行为依赖 add-lambda-l2 的 lifted function + env 设计，**完全不需要本 spec 改 IR**
- `[Benchmark]` runner 调度（real-time 跑 bench 方法、parse 输出）是独立 spec；本 spec 的 Validator F 阶段保证 [Benchmark] 函数签名正确，作为未来 runner mode 的基础
