# Tasks: Add Test Metadata Section

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-30
> 依赖 P0 (just/CI) + P1 (benchmark) 完成。
>
> 实施分 4 次提交（R1.A+B / R1.C.1 / R1.C.2-5 / R1.D）。详见底部"实施记录"。

## 进度概览

- [x] 阶段 1: C# 端 TestEntry 类型 + IrModule 字段
- [x] 阶段 2: zbc 二进制格式（section ID 注册 + Writer + Reader）
- [x] 阶段 3: AttributeBinder 集成（识别 6 个 attribute name；2 个推迟）
- [x] 阶段 4: Rust 端 test_index decoder
- [x] 阶段 5: LoadedArtifact 集成
- [x] 阶段 6: 跨语言契约测试
- [x] 阶段 7: 文档同步
- [x] 阶段 8: 验证全绿

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

---

## 实施记录（2026-04-30）

实际分 4 次提交完成，每次独立可回滚：

| Phase | Commit | 范围 |
|-------|--------|------|
| **R1.A+B** | `ea54554` | C# `TestEntry` / `TestEntryKind` / `TestFlags` 类型 + `IrModule.TestIndex` 字段 + `SectionTags.Tidx` + ZbcWriter/Reader v=1 plumbing；Rust 镜像类型 + `read_test_index` decoder + 10 单测 + `LoadedArtifact.test_index` 字段 |
| **R1.C.1** | `bb2df98` | 用户 feedback：`[Skip]` 不只是字符串，要区分 platform/feature。TIDX v=1 → v=2，TestEntry 加 `skip_platform_str_idx` + `skip_feature_str_idx`；C# + Rust 双端格式同步；Rust 单测扩到 12 个 |
| **R1.C.2-5** | `5180d21` | parser 重构 `TryParseNativeAttribute` → 公开 `TryParseAttribute` 返回 `(Native, Test)` 二选一；识别 6 个 z42.test attribute (`Test`/`Benchmark`/`Setup`/`Teardown`/`Ignore`/`Skip`)；新 AST 节点 `TestAttribute`；FunctionDecl 加 `TestAttributes` 字段；IrGen 加 `BuildTestIndex` / `BuildTestEntry` 写 IrModule.TestIndex；examples/test_demo.z42 + 跨语言契约测试 `test_demo_tidx_round_trips` |
| **R1.D** | (本 commit) | docs/design/zbc.md 加 TIDX section 二进制格式；docs/design/error-codes.md 注册 Z0911-Z0915 占位（R4 填实）；docs/design/testing.md 新建（含 Bench vs Test 分离原则）；归档到 spec/archive/2026-04-30-add-test-metadata-section/ |

### 实施过程偏差与决策

1. **TIDX section ID**：原 spec design.md 设计为数字 ID `0x0A`；调研发现 z42 zbc 用 4 字节 ASCII tag（NSPC/STRS/TYPE/...），改为 `TIDX` tag。
2. **二进制编码**：原 spec 设计为 LEB128 变长；改为固定宽度 LE（u32/u16/u8），与 zbc 其他 sections 一致（DBUG/FUNC 等都是固定宽度）。
3. **C# 文件位置**：原 spec 设计为 `src/compiler/z42.IR/Metadata/TestEntry.cs`；调研发现 z42.IR 项目目录扁平结构，按现有约定放顶层 `src/compiler/z42.IR/TestEntry.cs`。
4. **String pool 索引基准**：原 spec design.md 设 1-based（0=none）；调研 StringPool 是 0-based。保留 1-based 语义但显式 +1 偏移（IrGen 内 `Intern(s) + 1`）；reader 端读时按 1-based 解析。
5. **z42 attribute 系统不通用**：原 spec 假设 8 个 z42.test attribute 像 `[Native]` 一样可被识别；调研发现 z42 仅硬编码 `[Native]`，无通用 attribute 语法。决策：仿 [Native] 模式扩展 parser，识别 6 个简单 attribute（`[Test]`/`[Benchmark]`/`[Setup]`/`[Teardown]`/`[Ignore]`/`[Skip]`）。
6. **`[ShouldThrow<E>]` 与 `[TestCase(args)]` 推迟**：原 spec 计划 8 个 attribute；这两个需要 generic 语法 / typed args 解析支持。User 决策（决策 1A）：v0.1 仅 6 个简单形式；ShouldThrow / TestCase 留 R4 一并实施。
7. **TIDX v=1 → v=2**：用户 feedback "skip 不光是字符串"。R1.A+B 刚发的 v=1 在 R1.C.1 立即 bump 到 v=2 加 `skip_platform_str_idx` / `skip_feature_str_idx` 字段。无 v=1 文件曾被实际写入磁盘（parser 支持在 R1.C 才加），decoder 显式拒收 v=1。
8. **Bench 与 Test 分离原则**：R1.D testing.md 文档化"bench 不放 src/tests/"原则，与 Rust/C++/.NET/Java/Haskell 主流静态语言一致；详见 [docs/design/testing.md](../../docs/design/testing.md) Bench-vs-Test 章节。
9. **Spec 偏差记录方式**：用户决策（决策 3A）：实施记录在 commit + tasks.md，归档时入正式 spec；不在每次偏差都重新审批 spec。

### 已知缺口（留 backlog）

- **`[ShouldThrow<E>]` / `[TestCase(args)]`** — 需要 generic / typed args attribute 语法，留 R4 一并实施
- **TestEntry.expected_throw_type_idx 实际填充** — R4 校验 `[ShouldThrow<E>]` 时填实
- **TestEntry.test_cases 实际填充** — R4 实施 `[TestCase(args)]` 时填实
- **类外（top-level static）测试函数的 method_id 解析** — 当前已支持；class methods 也支持
- **zpkg 加载场景 test_index 聚合** — load_zpkg 暂返回空 vec；R3 runner 直接读 .zbc，不依赖 zpkg 路径

### 验证记录

- `cargo test --lib metadata::test_index` — 12/12 ✅
- `cargo test --test zbc_compat test_demo_tidx_round_trips` — ✅（编译 examples/test_demo.z42 → Rust 读 8 entries 一致）
- `just test` — 872/872 ✅（767 compiler xUnit + 104 vm golden + 1 cross-zpkg）
- 二进制 .zbc 含 `TIDX` magic + version=2 + 8 entries（hex dump 验证）
