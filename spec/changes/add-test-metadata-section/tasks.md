# Tasks: Add Test Metadata Section

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P0 (just/CI) + P1 (benchmark) 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: C# 端 TestEntry 类型 + IrModule 字段
- [ ] 阶段 2: zbc 二进制格式（section ID 注册 + Writer + Reader）
- [ ] 阶段 3: AttributeBinder 集成（识别 8 个 attribute name）
- [ ] 阶段 4: Rust 端 test_index decoder
- [ ] 阶段 5: LoadedArtifact 集成
- [ ] 阶段 6: 跨语言契约测试
- [ ] 阶段 7: 文档同步
- [ ] 阶段 8: 验证全绿

---

## 阶段 1: C# TestEntry 类型

- [ ] 1.1 [src/compiler/z42.IR/Metadata/TestEntry.cs](src/compiler/z42.IR/Metadata/TestEntry.cs) 新建：`TestEntryKind` enum / `TestFlags` flags / `TestEntry` record / `TestCase` record
- [ ] 1.2 [src/compiler/z42.IR/IrModule.cs](src/compiler/z42.IR/IrModule.cs) 加 `IReadOnlyList<TestEntry> TestIndex`（默认 `Array.Empty<>`）
- [ ] 1.3 验证：`dotnet build src/compiler/z42.IR/` 通过

## 阶段 2: zbc 二进制格式

- [ ] 2.1 调研：当前最大 section ID 是多少？记到 design.md（实施前确认）
- [ ] 2.2 [src/compiler/z42.IR/BinaryFormat/Sections.cs](src/compiler/z42.IR/BinaryFormat/Sections.cs)（按现有命名）注册 `TestIndex = 0x0A`
- [ ] 2.3 [src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs](src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs) 加 `WriteTestIndex` 方法；仅当 module.TestIndex.Count > 0 时调用
- [ ] 2.4 [src/compiler/z42.IR/BinaryFormat/ZbcReader.cs](src/compiler/z42.IR/BinaryFormat/ZbcReader.cs) 加 `ReadTestIndex` 方法；section dispatch 加 case
- [ ] 2.5 [src/compiler/z42.IR/BinaryFormat/ZbcVersion.cs](src/compiler/z42.IR/BinaryFormat/ZbcVersion.cs) bump zbc format version

## 阶段 3: AttributeBinder

- [ ] 3.1 调研：现有 attribute binding 在哪个文件？记到 design.md
- [ ] 3.2 [src/compiler/z42.Semantics/TestAttributeNames.cs](src/compiler/z42.Semantics/TestAttributeNames.cs) 新建：8 个 attribute 名常量 + `IsTestAttr` helper
- [ ] 3.3 修改 AttributeBinder（位置由 3.1 调研确定）：识别 z42.test.* attribute → 收集 TestEntry → 写到 IrModule.TestIndex
  - 处理 `[Skip(reason)]` 的 reason 字符串入字符串池
  - 处理 `[ShouldThrow<E>]` 的 E 类型 lookup（暂不校验 E 是否合法 Exception；R4 加）
  - 处理 `[TestCase(args)]` 多实例（同一函数多个 attribute → TestEntry.test_cases 多元素）
- [ ] 3.4 method_id 解析：emitter 完成函数 emit 后回填 TestEntry.method_id

## 阶段 4: Rust test_index decoder

- [ ] 4.1 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) 加 `bitflags = "2"`（如未有）
- [ ] 4.2 [src/runtime/src/metadata/test_index.rs](src/runtime/src/metadata/test_index.rs) 新建：
  - 常量 `TEST_INDEX_MAGIC` / `TEST_INDEX_VERSION`
  - `TestEntryKind` enum (`#[repr(u8)]`)
  - `TestFlags` bitflags
  - `TestEntry` / `TestCase` 结构体
  - `read_test_index(payload: &[u8]) -> Result<Vec<TestEntry>>`
- [ ] 4.3 [src/runtime/src/metadata/test_index_tests.rs](src/runtime/src/metadata/test_index_tests.rs)（独立文件，按 [.claude/rules/runtime-rust.md](.claude/rules/runtime-rust.md)）：5+ 单测
- [ ] 4.4 [src/runtime/src/metadata/mod.rs](src/runtime/src/metadata/mod.rs) `pub mod test_index;` + re-export

## 阶段 5: LoadedArtifact 集成

- [ ] 5.1 [src/runtime/src/metadata/loader.rs](src/runtime/src/metadata/loader.rs) `LoadedArtifact` 加 `pub test_index: Vec<TestEntry>` 字段
- [ ] 5.2 `load_artifact` 内部读 zbc TestIndex section（缺失时空 vec）
- [ ] 5.3 [src/runtime/src/metadata/formats.rs](src/runtime/src/metadata/formats.rs) 加 TestIndex section 解析 dispatch

## 阶段 6: 跨语言契约测试

- [ ] 6.1 [examples/test_demo.z42](examples/test_demo.z42) 新建：含 8 种 attribute 的最小示例
  - 至少 1 个 [Test] / [Benchmark] / [Setup] / [Teardown]
  - 1 个 [Test][Skip(reason)]
  - 1 个 [Test][Ignore]
  - 1 个 [Test][ShouldThrow<E>]
  - 1 个 [Test][TestCase(...)] × 3
- [ ] 6.2 [src/compiler/z42.Tests/AttributeBinderTests.cs](src/compiler/z42.Tests/AttributeBinderTests.cs) 加 8 个识别测试 + 1 个不识别非 test attribute 测试
- [ ] 6.3 [src/compiler/z42.Tests/TestIndexRoundTripTests.cs](src/compiler/z42.Tests/TestIndexRoundTripTests.cs) 新建：序列化往返、空 TestIndex、旧 zbc 兼容
- [ ] 6.4 [src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) 加跨语言契约测试（C# 写、Rust 读、比对）

## 阶段 7: 文档同步

- [ ] 7.1 [docs/design/ir.md](docs/design/ir.md) 加 "TestIndex Section" 章节
- [ ] 7.2 [docs/design/testing.md](docs/design/testing.md) 新建：测试框架总览（R1-R4 架构图 + 各 spec 范围）
- [ ] 7.3 [docs/design/error-codes.md](docs/design/error-codes.md) 加 Z0911-Z0915 占位
- [ ] 7.4 [docs/roadmap.md](docs/roadmap.md) Pipeline 进度表更新

## 阶段 8: 验证

- [ ] 8.1 `dotnet build src/compiler/z42.slnx` 通过
- [ ] 8.2 `cargo build --manifest-path src/runtime/Cargo.toml` 通过
- [ ] 8.3 `dotnet test src/compiler/z42.Tests/` 全绿（含新 AttributeBinderTests + TestIndexRoundTripTests）
- [ ] 8.4 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿（含新 test_index_tests）
- [ ] 8.5 `./scripts/test-vm.sh` 全绿（不含测试 attribute 的程序行为完全不变）
- [ ] 8.6 `./scripts/test-cross-zpkg.sh` 全绿
- [ ] 8.7 跨语言契约测试通过：编译 examples/test_demo.z42 → Rust 读 TestIndex → 8 个 entry 全部正确解码
- [ ] 8.8 binary diff：含 [Test] 的 .zbc 比不含的大；多出的字节正好是 TestIndex section
- [ ] 8.9 `dotnet build` 时长不显著上升（< 5%；attribute 收集是 O(N) over functions）

## 备注

### 实施依赖

- P0 (just/CI) 完成 ✅
- P1 (benchmark) 完成 ✅
- 不依赖其他在途工作

### 与其他 sub-spec 的关系

- **本 spec 替换** [add-z42-test-runner](../add-z42-test-runner/) 的"运行时发现"路径（已标 SUPERSEDED）
- **后续 R2** 实现 z42.test 库的 Assert/TestIO API（消费 TestEntry 数据，触发被测函数）
- **后续 R3** 实现 z42-test-runner 工具（直接读 LoadedArtifact.test_index 调度）
- **后续 R4** 加 attribute 语义校验（Z0911-Z0915 实际诊断逻辑）

### 风险

- **风险 1**：现有 AttributeBinder 位置不明 → 实施第 1 步先调研，记入 design.md
- **风险 2**：method_id 在 IR codegen 阶段才定，需要 emitter 与 binder 协调 → 实施时先 prototype 流程
- **风险 3**：zbc format version bump 让 stdlib 已构建的 .zpkg 失效 → 实施后跑一次 `just clean && just build && build-stdlib.sh` 确保链路通
- **风险 4**：bitflags crate 引入失败（msrv 等） → fallback 用裸 u16 + 常量

### 工作量估计

3-4 天：
- 调研 AttributeBinder 现状 + emitter 协调：0.5 天
- C# TestEntry 类型 + Reader/Writer：1 天
- AttributeBinder 修改：0.5 天
- Rust decoder + LoadedArtifact 集成：0.5 天
- 跨语言契约测试 + 调试：1 天
- 文档：0.5 天
