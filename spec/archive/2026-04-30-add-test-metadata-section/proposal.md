# Proposal: Add Test Metadata Section to zbc (Compile-Time Test Discovery)

## Why

z42 当前**无测试框架**，golden 用 stdout 字符串字面量比对，无 assertion 表达力。早先的 [add-z42-test-runner](../add-z42-test-runner/) (P2) 计划用**运行时**扫 `.zbc` 方法表识别 `[Test]` attribute，但这与 Rust libtest / Go testing 等成熟语言的**编译时**收集模式偏离。

参考 Rust libtest：编译器（`cargo build --tests`）扫所有 `#[test]` 收集到一个静态 vector，运行时直接读。优势：

1. **签名错误编译期就报**：`[Test] fn bad(x: i32)` 不到 runner 启动就被拦下
2. **零运行时扫描成本**：runner 启动直接读元数据 section，O(1)
3. **可静态校验**：`[ShouldThrow<E>]` 中的 E 是否真存在、`[Setup]` 函数签名是否正确，编译期就确定
4. **可被独立工具消费**：IDE 测试 lens、coverage 工具不需要复刻 runner 的运行时扫描

R1 引入 zbc 文件中的 `TestIndex` 二进制 section + 编译器侧的 attribute 收集器 + Rust 端的 reader API。**本 spec 不实现任何 runner / Assert API / 验证规则** —— 那些是 R2 / R3 / R4 的范围。R1 只交付**接口骨架**：

- C# 侧：`AttributeBinder` 识别 `[Test]` / `[Benchmark]` / `[Skip]` / `[Ignore]` / `[ShouldThrow]` / `[Setup]` / `[Teardown]` / `[TestCase]` 8 个 attribute 名，写入 `TestIndex` section
- 二进制格式：`TestIndex` section 进 zbc，含 entry 数组 + 每 entry 的 method_id / kind / flags
- Rust 侧：`zbc::read_test_index(zbc_bytes) -> Vec<TestEntry>`，被 R3 runner 消费

实际行为（attribute 含义、Assert 实现、签名校验细则）在 R2/R3/R4 中实现；R1 内**所有 runner 调用都触发 `not yet implemented` Trap**。

## What Changes

- **C# 侧**：
  - [src/compiler/z42.IR/Metadata/](src/compiler/z42.IR/Metadata/) 加 `TestEntry`、`TestEntryKind`、`TestFlags` 类型
  - [src/compiler/z42.IR/BinaryFormat/](src/compiler/z42.IR/BinaryFormat/) 加 `TestIndex` section 编码（紧接现有 sections，version bump）
  - `AttributeBinder` 识别 8 个 z42.test attribute name → 收集到 `TestEntry` 列表 → 写入 section
  - 攻击面：本 spec **不**做任何 attribute 语义校验（错签名 / 不存在的异常类型等）—— 都是 R4 的事。本期接受任何能 parse 的 attribute 写法
- **Rust 侧**：
  - 新建 [src/runtime/src/metadata/test_index.rs](src/runtime/src/metadata/test_index.rs)：`TestEntry` 结构体 + `read_test_index(&[u8]) -> Result<Vec<TestEntry>>` decoder
  - [src/runtime/src/metadata/mod.rs](src/runtime/src/metadata/mod.rs) 暴露 API
  - **不**修改 interp / runner（runner 在 R3 写）
- **测试**：
  - C# 侧：`AttributeBinderTests.cs` 加 8 个 attribute name 收集测试 + `TestIndex` 序列化往返
  - Rust 侧：`test_index_tests.rs` 测 decoder
  - 跨语言：手写 .z42 文件含全部 8 个 attribute → 编译为 .zbc → Rust decoder 读出 entries 与 C# 写入一致
- **文档**：
  - [docs/design/ir.md](docs/design/ir.md) 加 `TestIndex` section 二进制格式
  - [docs/design/testing.md](docs/design/testing.md)（新建）总览测试框架架构（含 R1-R4 全图）
  - 错误码注册占位：Z0911–Z0915 留给 R4

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/Metadata/TestEntry.cs` | NEW | `TestEntry` record + `TestEntryKind` enum + `TestFlags` |
| `src/compiler/z42.IR/Metadata/IrModule.cs` | MODIFY | 加 `IReadOnlyList<TestEntry> TestIndex { get; init; }` 字段 |
| `src/compiler/z42.IR/BinaryFormat/Sections.cs` | MODIFY（若有该文件） | 注册 `TestIndex` section ID（紧接最大现有 ID） |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | 写 TestIndex section（仅当 module.TestIndex 非空） |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | MODIFY | 读 TestIndex section 回到 IrModule |
| `src/compiler/z42.IR/BinaryFormat/ZbcVersion.cs` | MODIFY | bump zbc format version（pre-1.0 直接 bump，无兼容路径） |
| `src/compiler/z42.Semantics/AttributeBinder.cs`（按现有命名） | MODIFY | 识别 z42.test 8 个 attribute name → 收集 TestEntry |
| `src/compiler/z42.Semantics/TestAttributeNames.cs` | NEW | 静态常量集中 8 个 attribute name + 命名空间限定 |
| `src/compiler/z42.Tests/AttributeBinderTests.cs` | MODIFY 或 NEW | 加测试覆盖 8 个 attribute 收集 |
| `src/compiler/z42.Tests/TestIndexRoundTripTests.cs` | NEW | TestIndex 序列化 + 反序列化往返 |
| `src/runtime/src/metadata/test_index.rs` | NEW | Rust 侧 TestEntry + decoder |
| `src/runtime/src/metadata/test_index_tests.rs` | NEW | decoder 单测 |
| `src/runtime/src/metadata/mod.rs` | MODIFY | re-export test_index |
| `src/runtime/src/metadata/loader.rs` | MODIFY | `LoadedArtifact` 加 `test_index: Vec<TestEntry>` 字段；从 zbc 读出 |
| `src/runtime/tests/zbc_compat.rs` | MODIFY | 加跨语言契约测试：C# 写 TestIndex → Rust 读，比对一致 |
| `docs/design/ir.md` | MODIFY | 加 `TestIndex` section 二进制格式描述 |
| `docs/design/testing.md` | NEW | 测试框架总览（含 R1-R4 架构图与本 spec 在其中位置） |
| `docs/design/error-codes.md` | MODIFY | 注册 Z0911–Z0915 占位（语义留 R4 填） |
| `examples/test_demo.z42` | NEW | 含 8 种 attribute 的最小示例（编译演示，不能执行） |

**只读引用**：
- [src/compiler/z42.IR/BinaryFormat/](src/compiler/z42.IR/BinaryFormat/) 现有 section 列表（避免 ID 冲突）
- [src/runtime/src/metadata/formats.rs](src/runtime/src/metadata/formats.rs) — Rust 端 zbc 格式镜像类型
- [docs/design/ir.md](docs/design/ir.md) 现有 section 描述
- [src/runtime/src/lib.rs](src/runtime/src/lib.rs) — 理解 metadata 模块导出

## Out of Scope

- **`[Test]` 函数签名校验**（`fn() -> void` 强制等）→ R4
- **`[ShouldThrow<E>]` 中 E 是否合法异常类型** → R4
- **`[Setup]` / `[Teardown]` 是否在每个 test 前后调用** → R3 (runner 行为)
- **Assert API 实现** → R2 (z42.test 库)
- **`TestIO.captureStdout` 实现** → R2 + interp 侧 IO sink hook
- **runner 工具实现** → R3
- **golden 用例迁移 / 重写** → R5
- **运行时 Trap when invoking unimplemented test** → 保留现有 interp 行为（不强制）
- **JIT 端对 TestIndex 的处理** → 不需要（test 函数仍走 interp 路径；JIT 只优化函数体）

## Open Questions

- [ ] **Q1**：`TestIndex` section ID 用什么数字？
  - 倾向：取当前最大 ID + 1；具体值在 design.md 锁
- [ ] **Q2**：attribute name 用 `"z42.test.Test"` 完整限定还是仅 `"Test"`？
  - 倾向：完整限定（避免与用户自定义 attribute 冲突）
- [ ] **Q3**：`TestEntry.kind` 用 `enum u8` 还是 magic 字符串？
  - 倾向：`enum u8`（紧凑、类型安全；C# 端 `TestEntryKind` enum 与 Rust 端 `#[repr(u8)]` 镜像）
- [ ] **Q4**：`[TestCase(args)]` 的参数列表如何编码？
  - 倾向：每个 TestCase 实例单独一个 TestEntry，`flags` 标记 "TestCase variant"，`test_cases` 字段存参数 JSON 序列化（暂用 string；R4 时改为 typed）
- [ ] **Q5**：是否要加 `is_doctest: bool` 字段为 v0.2 doc-test 留空间？
  - 倾向：当前 `kind` enum 加 `Doctest = 5` variant 占位，但本期不写入；编译器侧识别但不收集
- [ ] **Q6**：`AttributeBinder` 是新建文件还是现有？
  - 倾向：先看现有命名（可能是 `AttributeBinder.cs` 或在 `TypeChecker` 内）；调研后定
