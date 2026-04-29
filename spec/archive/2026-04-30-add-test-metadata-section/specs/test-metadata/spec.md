# Spec: Test Metadata Section

## ADDED Requirements

### Requirement: 编译器识别 8 个测试 attribute

#### Scenario: 识别 `[Test]`

- **WHEN** .z42 函数前标 `[Test]` (完整限定 `z42.test.Test`)
- **THEN** 编译器收集为 `TestEntry { kind: Test, method_id: <fn_id> }`
- **AND** 写入 zbc 的 TestIndex section

#### Scenario: 识别 `[Benchmark]`

- **WHEN** 函数标 `[Benchmark]`
- **THEN** 收集为 `TestEntry { kind: Benchmark, method_id: <fn_id> }`

#### Scenario: 识别 `[Setup]` / `[Teardown]`

- **WHEN** 函数标 `[Setup]` 或 `[Teardown]`
- **THEN** 各自收集为 `kind: Setup` / `kind: Teardown` 的 TestEntry

#### Scenario: 识别 `[Skip(reason)]`

- **WHEN** 函数同时标 `[Test]` 和 `[Skip(reason: "blocked")]`
- **THEN** TestEntry.flags 含 `Skipped` bit
- **AND** TestEntry.skip_reason_str_idx 指向字符串池中 "blocked"

#### Scenario: 识别 `[Ignore]`

- **WHEN** 函数同时标 `[Test]` 和 `[Ignore]`
- **THEN** TestEntry.flags 含 `Ignored` bit

#### Scenario: 识别 `[ShouldThrow<E>]`

- **WHEN** 函数同时标 `[Test]` 和 `[ShouldThrow<DivByZero>]`
- **THEN** TestEntry.flags 含 `ShouldThrow` bit
- **AND** TestEntry.expected_throw_type_idx 指向 `DivByZero` 类型

#### Scenario: 识别 `[TestCase(args)]` 多实例

- **WHEN** 函数标 `[Test] [TestCase(0, 0)] [TestCase(1, 1)] [TestCase(10, 55)]`
- **THEN** 该函数对应一个 TestEntry，含 3 个 `test_cases[]` 元素
- **AND** 每个 TestCase.arg_repr_str_idx 指向参数的字符串表示

#### Scenario: 不识别非 z42.test attribute

- **WHEN** 函数标 `[MyApp.MyAttr]`（非 z42.test 命名空间）
- **THEN** 不出现在 TestIndex 中
- **AND** 不影响其他正常 attribute 处理

---

### Requirement: zbc 二进制格式

#### Scenario: 含测试函数时写入 TestIndex section

- **WHEN** 编译一个含 `[Test]` 的 .z42 文件
- **THEN** 产出 .zbc 含 section ID = 0x0A 的 TestIndex section
- **AND** payload 以 magic `0x54494458`（"TIDX"）开头
- **AND** payload version 字段 = 1

#### Scenario: 无测试函数时不写入 section

- **WHEN** 编译一个不含任何 z42.test attribute 的 .z42 文件
- **THEN** 产出 .zbc **不**含 TestIndex section（向前兼容工具）

#### Scenario: section 尾部有 LEB128 编码 entry_count

- **WHEN** 解析 TestIndex section
- **THEN** 紧随 magic + version 的是 LEB128 编码的 entry 数量

---

### Requirement: Rust 端 reader

#### Scenario: 解码合法 payload

- **WHEN** `read_test_index(valid_payload)` 调用
- **THEN** 返回 `Vec<TestEntry>`，长度等于编码时的 entry_count

#### Scenario: 拒绝错误 magic

- **WHEN** payload 起始字节非 `0x54494458`
- **THEN** `read_test_index` 返回 Err，message 含 "invalid magic"

#### Scenario: 拒绝不支持的 version

- **WHEN** payload version 字段不为 1
- **THEN** `read_test_index` 返回 Err，message 含 "unsupported test_index version"

#### Scenario: 处理空 section

- **WHEN** payload 是 `[magic][version=1][LEB128(0)]`
- **THEN** 返回空 `Vec<TestEntry>`，不报错

#### Scenario: 解码所有 kind variant

- **WHEN** payload 包含 5 种 TestEntryKind（Test/Benchmark/Setup/Teardown/Doctest）
- **THEN** 每个 entry 解码为对应 enum variant

---

### Requirement: LoadedArtifact 暴露 test_index

#### Scenario: 加载 zbc 时 test_index 自动填充

- **WHEN** `load_artifact(path)` 加载一个含 TestIndex 的 .zbc
- **THEN** 返回的 `LoadedArtifact` 含 `test_index: Vec<TestEntry>` 字段，元素与 zbc 中一致

#### Scenario: 加载无 TestIndex 的 zbc 时为空

- **WHEN** `load_artifact(path)` 加载不含 TestIndex section 的 .zbc
- **THEN** `LoadedArtifact.test_index` 是空 vec（`len() == 0`），不报错

---

### Requirement: 跨语言契约

#### Scenario: C# 写 → Rust 读 一致性

- **WHEN** 用 C# 编译器编译 [examples/test_demo.z42](examples/test_demo.z42)（含 8 种 attribute）
- **WHEN** Rust 端用 `read_test_index` 解码
- **THEN** entry 数量 = 期望（按 .z42 源码人工数）
- **AND** 每个 entry 的 kind / flags / skip_reason / expected_throw_type 与 .z42 源码语义一致

---

### Requirement: 文档

#### Scenario: ir.md 含 TestIndex 二进制格式

- **WHEN** 阅读 [docs/design/ir.md](docs/design/ir.md)
- **THEN** 含 "TestIndex Section" 章节，描述 section ID、magic、version、entry layout

#### Scenario: testing.md 含架构图

- **WHEN** 阅读 [docs/design/testing.md](docs/design/testing.md)
- **THEN** 含 R1-R4 全图（编译器收集 → zbc 元数据 → Rust reader → R3 runner 消费 → R2 z42.test 库 assertion）

#### Scenario: 错误码占位注册

- **WHEN** 阅读 [docs/design/error-codes.md](docs/design/error-codes.md)
- **THEN** Z0911-Z0915 占位条目存在，标注"R4 落地"

---

### Requirement: 不影响现有功能

#### Scenario: 现有 golden 测试全绿

- **WHEN** 实施完 R1 后跑 `just test`
- **THEN** 全部现有测试通过（含 dotnet test、test-vm、test-cross-zpkg）

#### Scenario: 不含 [Test] 的程序行为不变

- **WHEN** 编译一个无 z42.test attribute 的 .z42 文件
- **THEN** 产出 .zbc 二进制大小与 R1 实施前相同（不含 TestIndex section）
- **AND** z42vm 执行行为完全不变

#### Scenario: zbc format version bump

- **WHEN** 编译产出新 .zbc
- **THEN** zbc header 中的 format version 字段为新版本号
- **AND** 旧版 zbc (R1 实施前的) 不再被新 reader 加载（pre-1.0 不留兼容）
