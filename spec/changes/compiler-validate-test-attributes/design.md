# Design: Compiler-Side Test Attribute Validation

## Architecture

```
.z42 源码 → Lex → Parse → TypeCheck → AttributeBinder (R1) → ★ TestAttributeValidator (R4) ★ → IrGen → ZbcWriter
                                                                       │
                                                                       └─ 校验失败 → DiagnosticBag.Error → 编译失败
```

R4 加一个 pass 在 R1 (AttributeBinder) 之后、IrGen 之前。SemanticModel 已构建（TypeChecker 跑过），可 lookup E 类型 / 函数签名。

## Decisions

### Decision 1: Z0911 — [Test] 签名校验（锁定）

```csharp
class TestAttributeValidator
{
    void ValidateTestSignature(FunctionSymbol fn, DiagnosticBag diags)
    {
        if (fn.Parameters.Count != 0)
            diags.Error("Z0911", fn.Span, $"[Test] function '{fn.Name}' has invalid signature: must be fn() -> void (no parameters)");
        if (fn.ReturnType != BuiltinTypes.Void)
            diags.Error("Z0911", fn.Span, $"[Test] function '{fn.Name}' has invalid signature: must return void");
        if (fn.IsGeneric)
            diags.Error("Z0911", fn.Span, $"[Test] function '{fn.Name}' must not be generic");
        if (fn.IsInstanceMethod)
            diags.Error("Z0911", fn.Span, $"[Test] function '{fn.Name}' must be a free function or static method, not instance method");
    }
}
```

例外：标 `[TestCase(args)]` 时允许函数有参数，但参数类型必须能反序列化（暂留 v0.2）。本期 R4 只允许：
- 无 [TestCase]：fn() -> void
- 有 [TestCase]：参数数量 == TestCase args 数量；类型校验留 v0.2

### Decision 2: Z0912 — [Benchmark] 签名校验

```csharp
void ValidateBenchmarkSignature(FunctionSymbol fn, DiagnosticBag diags)
{
    var benchType = SemanticModel.LookupType("z42.test.Bencher");
    if (benchType == null)
    {
        diags.Error("Z0912", fn.Span, "[Benchmark] requires z42.test.Bencher; ensure z42.test is imported.");
        return;
    }

    // First param must be Bencher
    if (fn.Parameters.Count == 0 || fn.Parameters[0].Type != benchType)
        diags.Error("Z0912", fn.Span, $"[Benchmark] function '{fn.Name}' must take Bencher as first parameter");

    if (fn.ReturnType != BuiltinTypes.Void)
        diags.Error("Z0912", fn.Span, $"[Benchmark] function '{fn.Name}' must return void");
}
```

允许 `fn(Bencher) -> void` 与 `fn(Bencher, T) -> void`（后者搭配 [BenchmarkParam(T)]）。

### Decision 3: Z0913 — [ShouldThrow<E>] 校验

```csharp
void ValidateShouldThrow(FunctionSymbol fn, AttributeUsage shouldThrowAttr, DiagnosticBag diags)
{
    var typeArg = shouldThrowAttr.TypeArguments.FirstOrDefault();
    if (typeArg == null)
    {
        diags.Error("Z0913", shouldThrowAttr.Span, "[ShouldThrow<E>] requires a type argument E");
        return;
    }

    var resolvedType = SemanticModel.ResolveType(typeArg);
    if (resolvedType == null)
    {
        diags.Error("Z0913", shouldThrowAttr.Span, $"[ShouldThrow<{typeArg}>]: type '{typeArg}' not found");
        return;
    }

    var exceptionType = SemanticModel.LookupType("z42.core.Exception");
    if (!resolvedType.IsSubtypeOf(exceptionType))
        diags.Error("Z0913", shouldThrowAttr.Span, $"[ShouldThrow<{typeArg}>]: '{typeArg}' must be a subtype of Exception");

    // Side-effect: write resolved type's index into TestEntry.expected_throw_type_idx
    fn.TestEntry.ExpectedThrowTypeIdx = SemanticModel.GetTypeIndex(resolvedType);
}
```

R1 留的 `expected_throw_type_idx` 字段在本期填入实际类型索引（R1 占位为 0）。

### Decision 4: Z0914 — [Skip] reason 校验

```csharp
void ValidateSkip(AttributeUsage skipAttr, DiagnosticBag diags)
{
    var reason = skipAttr.NamedArguments.GetValueOrDefault("reason");
    if (reason == null || string.IsNullOrEmpty(reason as string))
        diags.Error("Z0914", skipAttr.Span, "[Skip] requires a non-empty reason: argument");
}
```

### Decision 5: Z0915 — [Setup] / [Teardown] 签名校验

```csharp
void ValidateSetupTeardown(FunctionSymbol fn, DiagnosticBag diags)
{
    if (fn.Parameters.Count != 0)
        diags.Error("Z0915", fn.Span, $"[Setup]/[Teardown] function '{fn.Name}' must take no parameters");
    if (fn.ReturnType != BuiltinTypes.Void)
        diags.Error("Z0915", fn.Span, $"[Setup]/[Teardown] function '{fn.Name}' must return void");
}
```

### Decision 6: Attribute 组合校验

```csharp
void ValidateAttributeCombinations(FunctionSymbol fn, DiagnosticBag diags)
{
    var attrs = fn.GetTestAttributes();

    // [Test] 与 [Benchmark] 互斥
    if (attrs.HasTest && attrs.HasBenchmark)
        diags.Error("Z0911", fn.Span, "function cannot be both [Test] and [Benchmark]");

    // [Setup]/[Teardown] 与 [Test]/[Benchmark] 互斥
    if ((attrs.HasSetup || attrs.HasTeardown) && (attrs.HasTest || attrs.HasBenchmark))
        diags.Error("Z0915", fn.Span, "[Setup]/[Teardown] cannot be combined with [Test] or [Benchmark]");

    // [Skip]/[Ignore] 必须有 [Test] 或 [Benchmark]
    if ((attrs.HasSkip || attrs.HasIgnore) && !(attrs.HasTest || attrs.HasBenchmark))
        diags.Error("Z0914", fn.Span, "[Skip]/[Ignore] requires [Test] or [Benchmark]");

    // [ShouldThrow] 必须有 [Test]
    if (attrs.HasShouldThrow && !attrs.HasTest)
        diags.Error("Z0913", fn.Span, "[ShouldThrow<E>] requires [Test]");

    // [TestCase] 参数数量校验
    foreach (var tc in attrs.TestCases)
    {
        if (tc.Args.Count != fn.Parameters.Count)
            diags.Error("Z0911", tc.Span, $"[TestCase] argument count {tc.Args.Count} doesn't match function parameter count {fn.Parameters.Count}");
    }
}
```

### Decision 7: Validator pass 在 PipelineCore 中位置

[src/compiler/z42.Pipeline/PipelineCore.cs](src/compiler/z42.Pipeline/PipelineCore.cs)：

```csharp
public static SourceCompileResult Compile(...)
{
    // ... (existing lex/parse/check) ...

    // R1: AttributeBinder 已收集 TestEntry
    // R4: 校验 attribute usage 合法性
    var testValidator = new TestAttributeValidator(diags, sem);
    testValidator.Validate(cu);
    if (diags.HasErrors)
        return new(null, diags, ...);

    // ... (existing irgen) ...
}
```

### Decision 8: 错误信息风格

参 z42 现有错误码风格（[docs/design/error-codes.md](docs/design/error-codes.md)）。每条错误：
- 错误码：Z091X
- 短描述：1 行
- 函数 + Span 引用源码位置
- 长描述：1-2 行解释 why + 建议修复

## Implementation Notes

### SemanticModel.LookupType("z42.test.Bencher")

需要 z42.test 库已链接（imported）。如果用户写 [Benchmark] 但没 import z42.test：
- 这本身就是错误（attribute 不识别 / 找不到 Bencher 类）
- 由现有 import resolver 报错（preexisting Z 编号），R4 不重复

### expected_throw_type_idx 回填

R1 的 TestEntry 保留了 expected_throw_type_idx 占位为 0；R4 在校验通过后写入实际 type idx。

### 组合校验的迭代

校验规则可能漏 case；实施时按 spec scenarios 一个个补，发现新组合 invariant 加 case。

## Testing Strategy

### 单元测试覆盖

[src/compiler/z42.Tests/TestAttributeValidatorTests.cs](src/compiler/z42.Tests/TestAttributeValidatorTests.cs) 每错误码至少：
- Positive: 合法 attribute 用法不报错
- Negative: 错误用法报对应 Z091X

完整测试矩阵：

| 测试 | 期望 |
|------|-----|
| `[Test] fn t() {}` | OK (no errors) |
| `[Test] fn t(x: i32) {}` | Z0911 |
| `[Test] fn t() -> i32 { return 0; }` | Z0911 |
| `[Test] fn t<T>() {}` | Z0911 |
| `[Benchmark] fn b(b: Bencher) {}` | OK |
| `[Benchmark] fn b() {}` | Z0912 |
| `[Test][ShouldThrow<DivByZero>] fn t() { 1/0 }` | OK |
| `[Test][ShouldThrow<NotAType>] fn t() {}` | Z0913 |
| `[Test][ShouldThrow<int>] fn t() {}` (int 不是 Exception) | Z0913 |
| `[Test][Skip(reason: "blocked")] fn t() {}` | OK |
| `[Test][Skip] fn t() {}` (缺 reason) | Z0914 |
| `[Setup] fn s() {}` | OK |
| `[Setup] fn s(x: i32) {}` | Z0915 |
| `[Test][Benchmark] fn x() {}` | Z0911 (互斥) |
| `[Test][Setup] fn x() {}` | Z0915 (互斥) |
| `[Skip(reason: "x")] fn x() {}` (无 [Test]) | Z0914 |
| `[Test][TestCase(1, 2)] fn t(a: i32, b: i32) {}` | OK |
| `[Test][TestCase(1)] fn t(a: i32, b: i32) {}` | Z0911 (arg count mismatch) |

### 跨 spec 验证

- 与 R5 整合：R5 的重写后 .z42 文件全部通过 R4 校验
- 与 R3 整合：R3 runner 假设输入合法（R4 已拦非法），不重复校验

### 文档校验

[docs/design/error-codes.md](docs/design/error-codes.md) 含每条 Z091X 完整描述（替换 R1 占位）。
