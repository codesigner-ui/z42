# Proposal: Generic Attribute Syntax `[Name<TypeArg>]` (R4.B)

## Why

R4.A 完成了 [Test] / [Benchmark] / [Setup] / [Teardown] / [Skip] / [Ignore] 6 个 z42.test.* attribute 的语法解析与签名校验。**唯一未实现的核心 attribute 是 `[ShouldThrow<E>]`** —— 它需要泛型 attribute 语法 `[Name<TypeArg>]`，z42 当前 parser 不支持。

具体阻塞点：

- [src/libraries/z42.test/tests/dogfood.z42](src/libraries/z42.test/tests/dogfood.z42) 中两个验证 `Assert.Fail` / `Assert.Skip` 抛异常路径的测试当前用 `[Skip(reason: "...verify via [ShouldThrow<TestFailure>] in R4")]` 占位
- z42.test 库**自检无法闭环**：抛 `TestFailure` / `SkipSignal` 的负向路径未自动验证
- TIDX section v=2 已经预留 `expected_throw_type_idx` 字段（R1.C 前瞻设计）；C# 侧 `TestEntry.ExpectedThrowTypeIdx` 已贯通；Rust loader 已经 resolve 字符串。**仅缺 parser → AST → IrGen 写入这一条链路**。

R4.B 让 `[ShouldThrow<TestFailure>]` 语法可用，编译期记录 expected throw 类型到 zbc，从而：

1. dogfood.z42 的两个 [Skip] 替换为 [ShouldThrow<E>]，z42.test 自检完整闭环
2. 后续 R3 完整版扩展 z42-test-runner 时直接读 zbc 的 `expected_throw_type` 即可（已在 LoadedArtifact 暴露）
3. 解锁后续可能的泛型 attribute（如 [TestCase<T>] / [Theory<T>]）

## What Changes

### 解析层

- **Lexer**：无新 token（`<` `>` `Identifier` 都是已有）
- **Parser**：[ParseTestAttributeBody](src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs#L266) 在 attribute 名之后、`(` 或 `]` 之前可选解析 `<TypeName>`
  - 语法形态：`[ShouldThrow<TestFailure>]` —— 单个类型参数
  - 不支持：多类型参数 `[X<A, B>]`（用例只需要单参，留给未来）
  - 不支持：嵌套 `[X<List<Y>>]`（同上）
  - 接受任意 z42.test.* attribute 名后跟 `<T>`，**语义校验**（哪些 attribute 允许有 type arg）由 R4.B validator 负责

### AST 层

- `TestAttribute` 记录加 `string? TypeArg` 字段
- 仅存类型名字符串（不持有 TypeExpr）—— 因为最终目标是写到 TIDX 字符串池，AST 里直接持字符串足够

### 语义层

- **TestAttributeValidator** 新增 E0913 检查：
  - `[ShouldThrow<E>]` 的 E 必须是已声明的 class 且继承自 `Exception`（直接或间接）
  - 只有 `[ShouldThrow]` 可以带 type arg；其他 attribute 带 `<T>` 报错
  - `[ShouldThrow]` 必须带 type arg（不能裸 `[ShouldThrow]`）

- **IrGen**：当 [ShouldThrow<E>] 出现时，把 E 的类型名加入 string pool，把 1-based idx 写入 `TestEntry.ExpectedThrowTypeIdx`（替换当前 `: 0` 占位）

### 测试

- z42.Tests 加 parser 测试（接受 `<T>` / 拒绝多参 / 拒绝裸 ShouldThrow）
- z42.Tests 加 validator 测试（E0913 三种触发场景：未声明 / 不继承 Exception / 错误 attribute 带 type arg）
- z42.Tests 加 IrGen → zbc → ZbcReader round-trip 测试（直接用 ZbcReader 验 ExpectedThrowTypeIdx + SHOULD_THROW flag）
- ⚠️ **dogfood.z42 替换不在本 spec scope**：在没有 runner ShouldThrow 比对（A2）的前提下，把 `[Skip]` 改成 `[ShouldThrow<E>]` 会让 `just test-stdlib` 把这两个测试报 Failed（runner 当前对 stderr 中 `TestFailure` 标记 Failed）。dogfood.z42 替换留给 A2 spec 与 runner 改动同 commit 落地

### Out of scope（明确不做）

- ⏸️ z42-test-runner 实际比对 thrown vs expected —— 留给独立 spec（A2: extend-runner-shouldthrow-runtime）
- ⏸️ 多类型参数 `[X<A, B>]`
- ⏸️ 嵌套泛型 `[X<List<Y>>]`
- ⏸️ TypeArg 改为 TypeExpr 持有（当前字符串足够；未来需要时再扩）
- ⏸️ user-defined attributes（z42 当前只有 z42.test.* + Native 两个白名单 attribute family）

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | `TestAttribute` 加 `TypeArg` 字段 |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | `TestAttributeNames` 加 `"ShouldThrow"`；`ParseTestAttributeBody` 接受 `<TypeName>` |
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | MODIFY | E0913 三种情况校验；E0911 互斥扩展（[ShouldThrow] 必须配 [Test] / [Benchmark]） |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | 当 [ShouldThrow<E>] 出现时，把 E 写入 string pool 并设 `TestEntry.ExpectedThrowTypeIdx` |
| `src/compiler/z42.Tests/Parser/TestAttributeParserTests.cs` | NEW | parser 单元测试 |
| `src/compiler/z42.Tests/Semantics/TestAttributeValidatorTests.cs` | MODIFY | 加 E0913 三种用例 |
| `src/compiler/z42.Tests/IR/TidxRoundtripTests.cs` | NEW or MODIFY existing | IrGen → ZbcWriter → ZbcReader 验证 ExpectedThrowTypeIdx 路径；不依赖 disasm |
| `docs/design/testing.md` | MODIFY | 增补 R4.B 段：generic attribute 语法 + ExpectedThrowType 流程 |

**只读引用**：

- `src/compiler/z42.IR/TestEntry.cs` — ExpectedThrowTypeIdx 字段已存在
- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` — TIDX emit 路径已存在（仅读取 entry.ExpectedThrowTypeIdx）
- `src/runtime/src/metadata/test_index.rs` — 已 resolve 字符串
- `src/runtime/src/metadata/loader.rs` — 同上
- `src/libraries/z42.test/src/Failure.z42` — TestFailure / SkipSignal 已存在

## Out of Scope

- 运行时 runner 比对 thrown vs expected —— 独立 spec（A2）
- 多类型参数 / 嵌套泛型 attribute
- user-defined attributes
- TIDX format 变更（v=2 字段已就绪，无需 bump）

## Open Questions

- [ ] ShouldThrow 与 Test 的搭配：必须有 [Test] 同时存在？还是允许独立？(预设：必须配 [Test] 或 [Benchmark]，类似 [Skip]/[Ignore] 的修饰符语义)
- [ ] E 必须是 namespace-qualified（`Std.TestFailure`）还是允许短名（`TestFailure` 通过 using 解析）？(预设：允许短名，与其他类型引用一致)
