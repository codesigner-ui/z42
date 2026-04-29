# Tasks: Extend z42.test Library

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 R1 (add-test-metadata-section) 完成。

## 进度概览

- [ ] 阶段 1: 调研 z42 attribute 系统现状
- [ ] 阶段 2: z42.test 库 attribute 类型
- [ ] 阶段 3: TestFailure / SkipSignal 异常类
- [ ] 阶段 4: Assert API（5 大语义组）
- [ ] 阶段 5: TestIO + native helpers (Rust)
- [ ] 阶段 6: Bencher + native helpers
- [ ] 阶段 7: corelib/io.rs sink 分流
- [ ] 阶段 8: 文档同步
- [ ] 阶段 9: 验证

---

## 阶段 1: 调研 z42 attribute 系统

- [ ] 1.1 grep `attribute` 关键字在 z42.Syntax / z42.Semantics 中的支持现状
- [ ] 1.2 确认是否支持 `[X(field: value)]` 命名参数语法
- [ ] 1.3 确认是否支持 `<T>` 泛型 attribute (`[ShouldThrow<E>]`)
- [ ] 1.4 调研结果记入 design.md "Implementation Notes"，按需细化或妥协

## 阶段 2: Attribute 类型定义

- [ ] 2.1 [src/libraries/z42.test/src/Test.z42](src/libraries/z42.test/src/Test.z42) 6 个 attribute (Test/Skip/Ignore/ShouldThrow/Setup/Teardown)
- [ ] 2.2 [src/libraries/z42.test/src/TestCase.z42](src/libraries/z42.test/src/TestCase.z42) TestCase
- [ ] 2.3 [src/libraries/z42.test/src/Benchmark.z42](src/libraries/z42.test/src/Benchmark.z42) Benchmark / BenchmarkParam
- [ ] 2.4 [src/libraries/z42.test/z42.test.toml](src/libraries/z42.test/z42.test.toml) 暴露 public types

## 阶段 3: 异常类

- [ ] 3.1 [src/libraries/z42.test/src/Failure.z42](src/libraries/z42.test/src/Failure.z42) TestFailure 类
- [ ] 3.2 SkipSignal 类（同文件）

## 阶段 4: Assert API

- [ ] 4.1 [src/libraries/z42.test/src/Assert.z42](src/libraries/z42.test/src/Assert.z42) 5.1-5.7 全部分组
  - 5.1 相等：eq / notEq / same / notSame
  - 5.2 布尔：isTrue / isFalse / isNull / isNotNull
  - 5.3 浮点：near / relativeNear / isFinite / isNaN
  - 5.4 集合：contains / notContains / empty / notEmpty / lengthEq / elementsEqual / elementsEquivalent
  - 5.5 字符串：startsWith / endsWith / matchesRegex / containsSubstring
  - 5.6 异常：throws / throwsWithMessage / doesNotThrow
  - 5.7 主动：fail / skip
- [ ] 4.2 内部 helper：`__value_to_string` 调用现有 corelib（如 convert.rs::value_to_str）
- [ ] 4.3 泛型约束 `<T: Equatable>` 等：如 z42 generics 未就绪，先用无约束 `<T>` + 运行时 ==

## 阶段 5: TestIO + Rust native helpers

- [ ] 5.1 [src/libraries/z42.test/src/TestIO.z42](src/libraries/z42.test/src/TestIO.z42) captureStdout / captureStderr / captureBoth
- [ ] 5.2 [src/runtime/src/corelib/test_io.rs](src/runtime/src/corelib/test_io.rs) 4 个 native：
  - `install_stdout_sink` / `take_stdout_buffer`
  - `install_stderr_sink` / `take_stderr_buffer`
  - 共用 thread-local 栈式 sink（design.md Decision 8）
- [ ] 5.3 [src/runtime/src/corelib/test_io_tests.rs](src/runtime/src/corelib/test_io_tests.rs) 单测：栈式语义、try_write_to_sink 行为
- [ ] 5.4 [src/runtime/src/corelib/mod.rs](src/runtime/src/corelib/mod.rs) 注册 dispatch + re-export

## 阶段 6: Bencher + Rust native helpers

- [ ] 6.1 [src/libraries/z42.test/src/Bencher.z42](src/libraries/z42.test/src/Bencher.z42) Bencher 类（iter / blackBox）
- [ ] 6.2 [src/runtime/src/corelib/bench.rs](src/runtime/src/corelib/bench.rs) `now_ns` / `black_box_value` native
- [ ] 6.3 [src/runtime/src/corelib/bench_tests.rs](src/runtime/src/corelib/bench_tests.rs) 单测：单调时钟、black_box 透传
- [ ] 6.4 [src/runtime/src/corelib/mod.rs](src/runtime/src/corelib/mod.rs) 注册 dispatch

## 阶段 7: corelib/io.rs 分流

- [ ] 7.1 [src/runtime/src/corelib/io.rs](src/runtime/src/corelib/io.rs) `println` / `print` / `write_line` 等检查 sink
- [ ] 7.2 [src/runtime/src/corelib/io_tests.rs](src/runtime/src/corelib/io_tests.rs) 加 sink 安装时 println 走 buffer 测试

## 阶段 8: 文档同步

- [ ] 8.1 [src/libraries/z42.test/README.md](src/libraries/z42.test/README.md) API 全集 + 示例
- [ ] 8.2 [docs/design/testing.md](docs/design/testing.md) 加 z42.test API 详解段
- [ ] 8.3 [docs/design/library-z42-test.md](docs/design/library-z42-test.md) 新建：内部机制（thread-local sink / native helpers / nested capture）
- [ ] 8.4 [docs/roadmap.md](docs/roadmap.md) Pipeline 进度表更新

## 阶段 9: 验证

- [ ] 9.1 `dotnet build src/compiler/z42.slnx` 通过
- [ ] 9.2 `cargo build --manifest-path src/runtime/Cargo.toml` 通过
- [ ] 9.3 `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] 9.4 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（含新 test_io_tests / bench_tests）
- [ ] 9.5 `./scripts/build-stdlib.sh` 把 z42.test 编译为 .zpkg
- [ ] 9.6 `./scripts/test-vm.sh` 全绿（不含 [Test] 的程序行为不变）
- [ ] 9.7 `./scripts/test-cross-zpkg.sh` 全绿
- [ ] 9.8 集成 demo：`examples/test_lib_demo.z42` 含 Assert + TestIO 调用 → 编译 + 手动调用 → 行为正确
- [ ] 9.9 嵌套 capture 行为符合栈式语义（手动验证）

## 备注

### 实施依赖

- R1 (add-test-metadata-section) 必须先落地，attribute 在编译器侧能识别为 z42.test.* name
- 不依赖 R3/R4/R5

### 与其他 sub-spec 的关系

- **本 spec 是 R3 的依赖**：runner 调用 Assert / TestIO / Bencher
- **不阻塞 R4**：R4 加签名校验，与库 API 设计正交

### 风险

- **风险 1**：z42 attribute 系统不完整 → 阶段 1 调研后调整 spec；可能要先做 R2.1 (extend-attribute-syntax) 再回来
- **风险 2**：z42 generics 未就绪 → Assert API 妥协用无约束泛型 + 运行时 ==
- **风险 3**：thread-local 在某些 z42 嵌入场景（如 wasm 单线程）行为差异 → wasm 仍单线程 thread-local 等价 thread-static，应可用
- **风险 4**：`Bencher.iter(closure)` 与 R3 runner 的 closure 提取协议不清 → 留 R3 设计 Bencher 调度时定；R2 阶段只声明 marker

### 工作量估计

3-4 天：
- attribute 调研 + 妥协决定：0.5 天
- z42 库代码（Test/Failure/Assert/TestIO/Bencher）：1.5 天
- Rust native + sink 分流：1 天
- 文档：0.5 天
- 集成验证：0.5 天
