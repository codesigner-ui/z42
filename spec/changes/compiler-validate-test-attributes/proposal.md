# Proposal: Compiler-Side Validation of Test Attributes

## Why

R1 让编译器**收集** `[Test]` / `[Benchmark]` / `[ShouldThrow<E>]` 等 attribute 写入 TestIndex section，但**不做任何语义校验**。这意味着：

```z42
[Test]
fn bad_test(x: i32) { ... }   // 错签名（[Test] 必须 fn() -> void）

[Test]
[ShouldThrow<NotAType>]       // NotAType 不存在
fn bad_throw() { ... }

[Skip]                         // 缺 reason 参数
fn skipped() { ... }
```

这些都能编译通过，错误延迟到 runner 启动甚至运行期才暴露。R4 在 `AttributeBinder` 之后加**校验 pass**，把上述错误前移到编译期，对应错误码 Z0911-Z0915（R1 已在 docs/design/error-codes.md 占位）。

## What Changes

### 校验规则（编译期 fail-fast）

| 错误码 | 触发条件 | 报错级别 |
|------|---------|---------|
| Z0911 | `[Test]` 函数签名不是 `fn() -> void` 或带泛型 | ERROR |
| Z0912 | `[Benchmark]` 函数签名不是 `fn(Bencher) -> void` 或 `fn(Bencher, T) -> void` | ERROR |
| Z0913 | `[ShouldThrow<E>]` 中 E 不存在 / 不是 Exception 子类型 | ERROR |
| Z0914 | `[Skip]` 缺 reason 参数 | ERROR |
| Z0915 | `[Setup]` / `[Teardown]` 函数签名不是 `fn() -> void` | ERROR |

附加校验：
- `[Test]` 与 `[Benchmark]` 不能同时标
- `[Setup]` / `[Teardown]` 不能与 `[Test]` / `[Benchmark]` 同时标
- `[Skip]` / `[Ignore]` 必须搭配 `[Test]` 或 `[Benchmark]` 使用
- `[ShouldThrow<E>]` 必须搭配 `[Test]`
- `[TestCase(args)]` 参数数量必须与函数签名匹配

### 实施位置

C# 端 `AttributeBinder`（R1 已修改）后接一个 `TestAttributeValidator` pass，在 `TypeChecker` 完成后跑（需 SemanticModel 已构建以 lookup 类型）。

发现问题 → 通过 `DiagnosticBag` 报错（沿用现有诊断机制）→ `dotnet build` 失败 + 显示 Z091X 错误码 + 引用源码位置。

### 错误信息示例

```
error Z0911: [Test] function 'bad_test' has invalid signature
   --> src/libraries/z42.io/tests/console.z42:5:1
    |
  5 | fn bad_test(x: i32) { ... }
    | ^^^^^^^^^^^^^^^^^^^
    | [Test] functions must be `fn() -> void` (no parameters, no return value).
    | If you want parameterized tests, use [TestCase(...)].
```

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/TestAttributeValidator.cs` | NEW | 5 类校验规则 + 组合规则 |
| `src/compiler/z42.Pipeline/PipelineCore.cs` | MODIFY | TypeCheck 后插入 TestAttributeValidator pass |
| `src/compiler/z42.Core/Diagnostics/ErrorCodes.cs`（按现有命名）| MODIFY | 注册 Z0911-Z0915 message |
| `src/compiler/z42.Tests/TestAttributeValidatorTests.cs` | NEW | 每个错误码至少 1 个 positive + 1 个 negative test |
| `docs/design/error-codes.md` | MODIFY | Z0911-Z0915 填入完整诊断信息（替换 R1 占位） |

**只读引用**：
- [add-test-metadata-section/](../add-test-metadata-section/) (R1) — TestEntry / TestAttributeNames
- [extend-z42-test-library/](../extend-z42-test-library/) (R2) — Bencher 类型（[Benchmark] 签名校验需引用）
- [src/compiler/z42.Core/Diagnostics/](src/compiler/z42.Core/Diagnostics/) — DiagnosticBag API

## Out of Scope

- **Runtime validation**（runner 侧再校验一次）→ 不必，编译期已拦
- **Quick-fix 建议**（IDE-friendly suggestion）→ v0.2
- **更多 attribute 组合校验** → 视实际需要扩
- **TestCase args 类型检查**（只校验数量；类型 mismatch 留给运行时） → v0.2

## Open Questions

- [ ] **Q1**：`[ShouldThrow<E>]` 中 E 必须显式 `: Exception` 还是任意类型？
  - 倾向：必须 Exception 子类型；Z0913 校验
- [ ] **Q2**：泛型函数能不能标 `[Test]`？（如 `[Test] fn test_x<T>()`）
  - 倾向：不能（runner 无法选 T）；Z0911 拦
- [ ] **Q3**：实例方法能不能标 `[Test]`？
  - 倾向：不能（runner 不实例化类）；Z0911 拦
- [ ] **Q4**：Z0914 `[Skip]` 缺 reason 是 ERROR 还是 WARNING？
  - 倾向：ERROR（强制写理由）
- [ ] **Q5**：是否在 R4 同时升级 TestEntry.expected_throw_type_idx 为 typed (从 string 到 type_idx)？
  - 倾向：是（R1 已留 idx 字段，本期填实数据）
