# Proposal: Extend z42.test Library (Assert + Attributes + TestIO + Bencher)

## Why

[src/libraries/z42.test/](src/libraries/z42.test/) 当前是占位库。R3 的 z42-test-runner 需要库提供：
- `[Test]` / `[Benchmark]` / `[Skip]` 等 attribute 类型（编译器已识别 attribute 名，但库要定义这些 type 才能 .z42 写）
- `Assert.eq` / `Assert.throws` 等 assertion 函数
- `TestIO.captureStdout` 让用户显式捕获输出
- `Bencher.iter(closure)` 给 `[Benchmark]` 函数用
- `TestFailure` 异常类型

R2 是 R1 编译时发现的**消费侧**：编译器写 TestEntry，库提供 .z42 用户面向 API，runner（R3）粘合两者。

## What Changes

### z42.test 库 .z42 源码

| 文件 | 内容 |
|------|------|
| `src/libraries/z42.test/src/Test.z42` | `[Test]` / `[Skip(reason)]` / `[Ignore]` / `[ShouldThrow<E>]` / `[Setup]` / `[Teardown]` 6 个 attribute 类型 |
| `src/libraries/z42.test/src/TestCase.z42` | `[TestCase(args)]` 参数化 attribute |
| `src/libraries/z42.test/src/Failure.z42` | `TestFailure : Exception` + 字段 (actual/expected/location/message) |
| `src/libraries/z42.test/src/Assert.z42` | 完整 Assert API（按 5.1-5.8 分组，详见 design.md） |
| `src/libraries/z42.test/src/TestIO.z42` | `captureStdout` / `captureStderr` / `captureBoth` |
| `src/libraries/z42.test/src/Bencher.z42` | `Bencher` 类 + `iter(closure)` 方法（criterion-style） |
| `src/libraries/z42.test/src/Benchmark.z42` | `[Benchmark]` / `[BenchmarkParam(value)]` attribute |
| `src/libraries/z42.test/z42.test.toml` | 暴露 public API |
| `src/libraries/z42.test/README.md` | 更新文档 |

### Native interop（为 TestIO 与 Bencher.iter 提供 host hook）

`TestIO.captureStdout` 与 `Bencher.iter` 都需要宿主端配合：
- `captureStdout` 需要 thread-local IO sink（Console.println 检测 sink 状态分流）
- `Bencher.iter` 需要高分辨率 timer 与 black_box 相关 native 函数

通过 native interop 暴露 4 个 native 函数：

| 函数 | 用途 |
|------|------|
| `__test_io_install_stdout_sink()` | runner 侧调用，开始捕获到 thread-local buffer |
| `__test_io_take_stdout_buffer() -> string` | 取出捕获内容 + 卸载 sink |
| `__bench_now_ns() -> u64` | 单调时钟纳秒级（measure cost） |
| `__bench_black_box<T>(value: T) -> T` | 防止 LLVM/JIT 优化被测代码 |

### Console.println 分流

[src/runtime/src/corelib/io.rs](src/runtime/src/corelib/io.rs) 的 `println` 检测 thread-local stdout sink，有则写 sink，无则 stdout。

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.test/src/Test.z42` | NEW | 6 个 attribute 类型 |
| `src/libraries/z42.test/src/TestCase.z42` | NEW | TestCase attribute |
| `src/libraries/z42.test/src/Benchmark.z42` | NEW | Benchmark + BenchmarkParam attribute |
| `src/libraries/z42.test/src/Failure.z42` | NEW | TestFailure 异常类 |
| `src/libraries/z42.test/src/Assert.z42` | NEW（或 MODIFY 占位） | Assert 类全部 API |
| `src/libraries/z42.test/src/TestIO.z42` | NEW | TestIO 类（3 capture 方法） |
| `src/libraries/z42.test/src/Bencher.z42` | NEW | Bencher 类 + iter 方法 |
| `src/libraries/z42.test/z42.test.toml` | MODIFY | 注册新公共类型 |
| `src/libraries/z42.test/README.md` | MODIFY | API 全集说明 |
| `src/runtime/src/corelib/io.rs` | MODIFY | println 检测 thread-local sink |
| `src/runtime/src/corelib/test_io.rs` | NEW | `__test_io_install_stdout_sink` / `__test_io_take_stdout_buffer` 两个 native |
| `src/runtime/src/corelib/bench.rs` | NEW | `__bench_now_ns` / `__bench_black_box` 两个 native |
| `src/runtime/src/corelib/mod.rs` | MODIFY | re-export test_io / bench 模块 + 注册 native dispatch |
| `src/runtime/src/corelib/io_tests.rs` | MODIFY | 加 sink 注入 / 取出 单测 |
| `src/runtime/src/corelib/bench_tests.rs` | NEW | now_ns 单调性 / black_box 防优化 单测 |
| `src/libraries/z42.test/tests/.gitkeep` | NEW（若无） | 占位（实际测试 R5 时补） |
| `docs/design/testing.md` | MODIFY | 加 z42.test API 详解 + native interop 列表 |
| `docs/design/library-z42-test.md` | NEW | z42.test 设计文档（API + 内部机制） |

**只读引用**：
- [add-test-metadata-section/](../add-test-metadata-section/) (R1) — attribute name 编译器识别已锁定
- [src/runtime/src/corelib/](src/runtime/src/corelib/) — 现有 native 接入模式
- [src/libraries/z42.collections/](src/libraries/z42.collections/) — 参考 stdlib 库结构

## Out of Scope

- **runner 工具实现** → R3
- **attribute 签名校验**（错签名报错）→ R4
- **golden 用例改写** → R5
- **`[Property]` property-based testing** → v0.3
- **`[Snapshot]` snapshot testing** → v0.2
- **z42.test 自身的单元测试**（dogfooding） → R5（迁移阶段补）
- **多线程隔离 IO sink**（thread-local 仅当前线程；多线程并行 runner 是 R6）→ 后期

## Open Questions

- [ ] **Q1**：`Assert.throws<E>` 的 E 是泛型类型参数（z42 已支持？）—— 若不支持，改为 `Assert.throws(typeName: string, action: fn() -> void)`
  - 倾向：先用 string 形式（z42 泛型尚在设计）；R4 时升级为 typed
- [ ] **Q2**：`Bencher.iter(closure)` 的 closure 类型签名？
  - 倾向：`fn() -> void`（最简）；后期支持有返回值的 closure
- [ ] **Q3**：`TestIO.captureStdout` 在嵌套调用时行为？（一个 capture 内再开一个 capture）
  - 倾向：内层 capture 独占 sink，外层暂停接收；用栈式 thread-local
- [ ] **Q4**：`__bench_black_box` 怎么实现？Rust 端 `std::hint::black_box` 仅 stable 1.66+
  - 倾向：用 `std::hint::black_box`（稳定）+ JIT 端用 cranelift `Function::has_side_effects`
- [ ] **Q5**：是否所有 native 函数都用 `__` 前缀？
  - 倾向：是（与现有 stdlib 风格一致）
