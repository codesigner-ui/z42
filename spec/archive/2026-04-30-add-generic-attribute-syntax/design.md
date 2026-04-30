# Design: Generic Attribute Syntax `[Name<TypeArg>]`

## Architecture

```
.z42 source           (Parser)              (Semantics)            (IrGen)              .zbc
  ┌────────────┐     ┌───────────┐         ┌────────────┐         ┌────────────┐
  │[Test]      │     │TestAttribute       │TestAttribute       │TestEntry          │TIDX section
  │[ShouldThrow│ ──► │  Name=    │ ──────► │  Validator │ ──────► │  ExpectedThrow│ ──────► v=2 payload
  │  <TestFail>│     │   "Should │         │  E0913     │         │   TypeIdx     │         (no bump)
  │]           │     │    Throw" │         │  checks    │         │   = pool[N]   │
  │void f(){}  │     │  TypeArg= │         │   - exists │         │  Flags |=     │
  └────────────┘     │   "TestFa │         │   - is Exc │         │   SHOULD_THROW│
                     │   ilure"  │         │   - paired │         └────────────┘
                     └───────────┘         │     w/Test │              │
                                           └────────────┘              │
                                                                       ▼
                                                         (Rust loader, R1.C 已就绪)
                                                          resolve_test_index_strings
                                                          → TestEntry.expected_throw_type
                                                              = Some("TestFailure")
```

## Decisions

### Decision 1: TypeArg 用 string 而非 TypeExpr

**问题**：`TestAttribute.TypeArg` 字段类型 —— `string?` 还是 `TypeExpr?`？

**选项**：
- A — **string?**：parser 把 `<TestFailure>` 解出来直接存 identifier 文本
- B — **TypeExpr?**：复用现有 `TypeExpr` 层次（NamedType / GenericType / ...）

**决定**：选 **A (string?)**。

**理由**：
- 最终目的是写到 TIDX string pool；string pool 本身就是字符串
- 当前需求只支持单层非泛型类型名（`TestFailure` / `Std.Exception`），TypeExpr 表达力浪费
- 升级路径清晰：未来要支持 `[ShouldThrow<List<E>>]` 时再换成 TypeExpr，现在的 string 字段也可以平滑迁移
- 验证阶段需要把 string 解析成"是否是已声明的 Exception 子类"，那一步直接走类型符号表 lookup（类似 `class X : Exception` 的 base 查找路径）

### Decision 2: Parser 接受任意 `[Name<T>]`，validator 限定哪些 attribute 允许

**问题**：parser 是只识别 `[ShouldThrow<T>]` 一种？还是允许任何 z42.test.* attribute 都接受 `<T>`？

**选项**：
- A — **parser 严格白名单**：只识别 `[ShouldThrow<T>]`，其他 attribute 出现 `<` 就 ParseException
- B — **parser 通用 + validator 限定**：parser 接受任何 `[Name<T>]`；validator 检查"这个 attribute 是否允许 type arg"

**决定**：选 **B**。

**理由**：
- parser 职责清晰（语法），semantics 职责清晰（哪些组合合法）
- 添加未来的泛型 attribute（如 [TestCase<T>]）不需要改 parser，只改 validator 白名单
- 错误消息更具体（"Test does not accept a type argument" vs 单调的 "unexpected `<`"）
- 与现有架构一致：parser 接受任何 `[Name(...)]` named args，validator 检查 reason 是否必填

### Decision 3: ShouldThrow 必须配 [Test] / [Benchmark]

**问题**：[ShouldThrow<E>] 单独使用是否合法？

**选项**：
- A — 修饰符语义：必须配 [Test] / [Benchmark]，类似 [Skip] / [Ignore]
- B — 独立语义：可以单独用，相当于 [Test] + 期望 throw

**决定**：选 **A (修饰符语义)**。

**理由**：
- 与 [Skip] / [Ignore] 一致，认知负担最小
- 单独使用 [ShouldThrow] 而无 [Test] 是模糊状态：不会被发现成 test，但 throw 期望永远不会被验证
- E0914 现有规则 "modifier requires primary attribute" 直接扩展到 [ShouldThrow]，零新错误码

**实现细节**：在 [TestAttributeValidator.cs](src/compiler/z42.Semantics/TestAttributeValidator.cs) 现有 E0914 检查里，把 `hasShouldThrow` 加入 `(hasSkip || hasIgnore)` 同组判断。

### Decision 4: ShouldThrow 类型短名 vs 全限定

**问题**：用户写 `[ShouldThrow<TestFailure>]` 还是 `[ShouldThrow<Std.TestFailure>]`？

**选项**：
- A — 允许短名：通过当前命名空间的 using 解析
- B — 强制全限定：`Std.TestFailure`

**决定**：选 **A (允许短名)**。

**理由**：
- 与 z42 其他类型引用一致（`class X : Exception` 不强制 `Std.Exception`）
- 用户体验自然
- TIDX 中存什么名？**存源码原文**（短名或全限定，看用户写法），由 runtime 比对时按需解析。R4.B scope 不做名称解析或规范化

**注**：validator 校验"是否继承 Exception"时**需要**类型符号表 lookup（这一步会走 using 解析）。但写到 TIDX 的字符串保留原始文本（保留信息，runtime 拿到后可自行规范化）。

### Decision 5: Parser 实现细节

`ParseTestAttributeBody` 在 advance 过 attribute 名后，插入 type-arg 解析步骤：

```csharp
cursor = cursor.Advance(); // <name>

string? typeArg = null;
if (cursor.Current.Kind == TokenKind.Lt)
{
    cursor = cursor.Advance(); // <
    if (cursor.Current.Kind != TokenKind.Identifier)
        throw new ParseException(
            $"`[{name}<...>]` requires a type identifier",
            cursor.Current.Span,
            DiagnosticCodes.UnexpectedToken);
    typeArg = cursor.Current.Text;
    cursor = cursor.Advance(); // <Type>
    // 拒绝多参 / 嵌套
    if (cursor.Current.Kind == TokenKind.Comma)
        throw new ParseException(
            $"`[{name}<...>]` accepts a single type parameter; multi-arg generics not supported in attributes",
            cursor.Current.Span,
            DiagnosticCodes.UnexpectedToken);
    if (cursor.Current.Kind == TokenKind.Lt)
        throw new ParseException(
            $"`[{name}<...>]` does not support nested generic type parameters",
            cursor.Current.Span,
            DiagnosticCodes.UnexpectedToken);
    ExpectKind(ref cursor, TokenKind.Gt);
}

// ... existing named-args parsing unchanged
```

**注**：z42 的 `<` 在表达式上下文有歧义（比较 vs 泛型）。但在 attribute 内部 `[Name<...>]` 上下文是无歧义的——`<` 紧跟在 identifier 后、attribute body 内，不会被当作比较运算符。无 lookahead 需求。

### Decision 6: IrGen 资源

`IrGen.cs` 当前在构建 TestEntry 时硬编码 `ExpectedThrowTypeIdx: 0`。需要：

1. 找到当前 function 的 `[ShouldThrow]` TestAttribute（已 collected）
2. 取 `TypeArg` string
3. 把 string 加入 string pool 拿 1-based idx
4. 写入 `TestEntry.ExpectedThrowTypeIdx`
5. 同时设置 `TestFlags.ShouldThrow` 位

string pool 在 IrGen 已有 `internalString(...) -> int` 类似 API（参 skip_reason 路径，已经在 ZbcWriter 中通过 `RemapTidxStrIdx` 处理 IrGen-pool → ZbcWriter-pool 映射）。

## Implementation Notes

### Parser 测试关注点

- `[ShouldThrow<TestFailure>]` 单条 → TypeArg="TestFailure"
- `[ShouldThrow<Std.TestFailure>]` —— **本 spec scope 不支持**（dotted name 在单 identifier 中无法表达）。先报 ParseException；如未来需支持，扩成 dotted ident 解析
- `[ShouldThrow]` 无类型参 → TypeArg=null（validator 报 E0913）
- `[ShouldThrow<>]` 空 → ParseException（identifier required）
- `[ShouldThrow<A, B>]` → ParseException（多参不支持）
- `[ShouldThrow<List<int>>]` → ParseException（嵌套不支持）
- `[Test<E>]` → 解析成功，TypeArg="E"；validator 报 E0913（attribute 不接受 type arg）

### Validator 测试关注点

- `[Test] [ShouldThrow<TestFailure>] void f() {}` → 通过
- `[ShouldThrow<TestFailure>] void f() {}` → E0914（缺 primary）
- `[Test] [ShouldThrow] void f() {}` → E0913（type arg required）
- `[Test] [ShouldThrow<NotAType>] void f() {}` → E0913（type 不存在）
- `[Test] [ShouldThrow<int>] void f() {}` → E0913（不继承 Exception）
- `[Test<E>] void f() {}` → E0913（attribute 不接受 type arg）

### IrGen 测试关注点

- 编译 `[Test] [ShouldThrow<TestFailure>] void f() {}` → 检查 .zbc 中 TIDX entry 的 ExpectedThrowTypeIdx 非 0、SHOULD_THROW flag 置位、resolved string == "TestFailure"

最直接的检查方式是 `dotnet z42c.dll disasm <file>.zbc | grep TIDX`，但当前 disasm 不显示 TIDX 内容，可能需要扩展或在 z42.Tests 直接调用 ZbcReader。

### dogfood.z42 替换

```diff
 [Test]
-[Skip(reason: "Assert.Fail throws TestFailure — verify via [ShouldThrow<TestFailure>] in R4")]
+[ShouldThrow<TestFailure>]
 void test_assert_fail_throws_testfailure() {
     Assert.Fail("expected to fail");
 }

 [Test]
-[Skip(reason: "Assert.Skip throws SkipSignal — verify via [ShouldThrow<SkipSignal>] in R4")]
+[ShouldThrow<SkipSignal>]
 void test_assert_skip_throws_skipsignal() {
     Assert.Skip("expected to skip");
 }
```

**注意**：替换后**当前 z42-test-runner 仍会把这两个测试报为 Failed**（runner 没有 ShouldThrow 比对逻辑，会捕获 stderr 中的 TestFailure / SkipSignal 抛出标记成失败）。这是预期行为：A1 spec 只关心**编译期记录**信息到 zbc，runtime 比对留给 A2 spec。

为避免 `just test-stdlib` 在 A1 之后失败，**dogfood.z42 替换这一步留到 A2 spec 实施时再做**——A1 spec 完成后的状态：parser/validator/IrGen 全部就绪，但 dogfood.z42 仍保留 [Skip] 占位。

> **修订**：把 dogfood.z42 替换从 A1 scope 移到 A2 scope。A1 实施 + 验证后，dogfood.z42 不变，避免临时回归。这一改动反映在下面 Scope 表与 tasks.md。

## Testing Strategy

- **单元测试**：
  - z42.Tests / Parser / `TestAttributeParserTests.cs`（NEW）—— 6 个 parser scenario（accept / reject / round-trip）
  - z42.Tests / Semantics / `TestAttributeValidatorTests.cs`（MODIFY）—— 加 6 个 validator scenario（覆盖 E0913 三种情况 + 三种合法/非法搭配）
- **IrGen / 二进制契约**：z42.Tests 加测试编译 `[ShouldThrow<TestFailure>]` 到 zbc 后检查 TestEntry.ExpectedThrowTypeIdx + flags 位（用 ZbcReader 读取，不依赖 disasm）
- **dotnet test**：必须全绿
- **VM golden**：A1 不动 z42 源码，golden 集合不变；`./scripts/test-vm.sh` 应继续 104/104 通过
- **stdlib lib 测试**：dogfood.z42 不变，`just test-stdlib` 应继续 5 passed / 2 skipped
