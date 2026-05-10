# Tasks: split-typechecker-tests

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 1 §1.4

## 验证报告

### 编译状态
- ✅ `dotnet build src/compiler/z42.Tests/z42.Tests.csproj`: 0 Warning / 0 Error

### 测试结果
- ✅ `dotnet test`: **1104/1104** 全绿（与 baseline 一致）

### 拆分等效性
- ✅ `[Fact]` 总数: 原 150 = 拆后 150（精确一致）

### LOC 目标
- 主 TypeCheckerTests.cs: 1730 → **177**（仅 helpers + Valid programs）
- 6 个 partial 文件全部 ≤ 600 软限：
  - Generics.cs: 483
  - Constraints.cs: 336
  - Errors.cs: 252
  - Native.cs: 227
  - Literals.cs: 201
  - Inheritance.cs: 126

### 结论：✅ 全绿，可归档

**变更说明**: 把 `src/compiler/z42.Tests/TypeCheckerTests.cs` (1730 LOC) 按现有 26 个 `// ── XXX` 分类头拆为 7 个 `partial class TypeCheckerTests` 文件，每个 ≤ 600 LOC（[code-organization.md](../../../.claude/rules/code-organization.md) 测试文件软限）。

**原因**: 单文件 1730 LOC 严重超 600 软限。按特性拆后导航成本降低，加新测试时定位明确（约束类测试 vs 字面量测试 vs 泛型测试 各有归属）。**纯文件搬运，零代码变化**。

**文档影响**:
- `docs/review.md` — 路线图 §编译器线 `split-large-test-files` 状态注记 C# 部分完成；rc_heap 部分待跟进
- `src/compiler/z42.Tests/` — 无 README（不在 [code-organization.md](../../../.claude/rules/code-organization.md) 强制 README 范围内：3 层目录 = src/compiler/z42.Tests/，没有继续下钻）

> **注意**: 本 spec 仅处理 C# 测试文件。`rc_heap_tests.rs` (1229 LOC) 留独立 spec `split-rc-heap-tests`（Rust 拆分模式不同：用子目录 `rc_heap_tests/<topic>.rs` + `mod` 声明）。

---

## Scope（允许改动的文件）

### MODIFY

| 文件 | 说明 |
|---|---|
| `src/compiler/z42.Tests/TypeCheckerTests.cs` | 主 partial：保留 `// Helpers` 段（行 1-32）+ Valid programs 段（行 400-543），其余切到子文件；声明改 `public partial class TypeCheckerTests` |
| `docs/review.md` | 路线图 `split-large-test-files` 状态注记（C# 部分已完成） |

### NEW (6 partial files)

| 文件 | 涵盖原行号 | 涵盖类别 | 预估 LOC |
|---|---|---|---|
| `TypeCheckerTests.Constraints.cs` | 33-159, 352-399, 1349-1497 | Ctor / Enum constraint, chain validation, ref/value, bare type-param | ~330 |
| `TypeCheckerTests.Generics.cs` | 160-306, 307-351, 1025-1076, 1077-1234, 1498-1566 | extern impl, operator overload, Generics, generic constraints L3-G2, instantiated generic substitution | ~580 |
| `TypeCheckerTests.Inheritance.cs` | 1235-1348 | Base class constraints L3-G2.5 | ~115 |
| `TypeCheckerTests.Errors.cs` | 544-732, 767-783 | Undefined / type mismatch / return / arity / array / void / duplicate / interface impl / arg types | ~210 |
| `TypeCheckerTests.Literals.cs` | 784-972 | Integer literal range checking + C# type aliases | ~190 |
| `TypeCheckerTests.Native.cs` | 973-1024, 1567-1633, 1634-1730 | [Native] / extern validation, primitive interface impl, static abstract interface members | ~220 |

**只读引用**:
- 各 partial 文件均依赖 `TypeCheckerTests` 主文件中的 helper 方法（`Wrap`, `WrapExpr` 之类）。这些助手保留在主文件中

---

## 设计要点

### partial class 累计计入 200 行硬限

[code-organization.md](../../../.claude/rules/code-organization.md) 说：
> partial class 累计计入：C# `partial class` / `partial struct` 的多个分部
> 文件按"类型整体"判定 200 行硬限——把所有 `partial X` 文件中归属同一类型的
> 行（除 using / namespace / `partial class X { ... }` 包裹之外的成员体积）
> 累加。

`TypeCheckerTests` 类整体本来就 1730 行（已远超 200），拆 partial 不能解决这个问题——但 **测试类传统上不受 200 行硬限约束**（单一主题测试集合天然会超过）。规则明确写"类型 200 行硬限"，没说测试类豁免，但工程实践上：

> 测试文件（`*_tests.rs`、`*Tests.cs`）不计入限制，但单个测试文件超过 600 行时也应按功能拆分。

注意：规则明文说"测试**文件**不计入限制"，没说测试**类型**。所以测试类型整体 1730 行是合规的（豁免 200 行硬限），但**单文件 1730 行超 600 软限**——这才是本 spec 处理的目标。拆 partial 把单文件压到 ≤ 600，类型整体仍然是大类型（豁免规则下合规）。

### 命名约定

`TypeCheckerTests.<Topic>.cs` — VS / Rider 会自动把所有 partial 文件归在主文件下展开。`<Topic>` 用 PascalCase 单词（`Constraints` 而非 `constraints`、`generic_constraints`）。

### 测试覆盖度保持

零代码变化 = 零测试丢失。`dotnet test` 应稳定 1104 → 1104 通过。

---

## 任务清单

### 阶段 1: 准备
- [ ] 1.1 baseline: `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 确认 1104 通过
- [ ] 1.2 列出 26 个 `// ── XXX` 分类头与对应行号，与 Scope 表对照（已完成）

### 阶段 2: 创建 6 个 partial 文件（按依赖拓扑序无所谓）
每个 partial 文件用相同模板:
```csharp
namespace Z42.Tests;

public partial class TypeCheckerTests
{
    // 搬运的测试方法 + 类别注释
}
```

- [ ] 2.1 `TypeCheckerTests.Constraints.cs` — 搬 ctor/enum/chain validation/ref-value/bare 共 5 段
- [ ] 2.2 `TypeCheckerTests.Generics.cs` — 搬 extern impl/operator/Generics/instantiated/G2 共 5 段
- [ ] 2.3 `TypeCheckerTests.Inheritance.cs` — 搬 Base class constraints
- [ ] 2.4 `TypeCheckerTests.Errors.cs` — 搬 undefined/mismatch/return/arity/array/void/dup/interface/arg types 共 9 段
- [ ] 2.5 `TypeCheckerTests.Literals.cs` — 搬 integer literal/aliases 共 2 段
- [ ] 2.6 `TypeCheckerTests.Native.cs` — 搬 [Native]/primitive interface/static abstract 共 3 段

### 阶段 3: 改造主文件
- [ ] 3.1 删除已搬出的代码段（保留行 1-32 helpers + 400-543 Valid programs）
- [ ] 3.2 主类声明从 `public class TypeCheckerTests` 改为 `public partial class TypeCheckerTests`
- [ ] 3.3 主文件 LOC 验证 ≤ 200 行（仅 helpers + 两段保留）

### 阶段 4: 验证
- [ ] 4.1 `dotnet build src/compiler/z42.Tests/z42.Tests.csproj` 无 warning
- [ ] 4.2 `dotnet test`: **1104/1104** 不变
- [ ] 4.3 `wc -l src/compiler/z42.Tests/TypeCheckerTests*.cs` 全部 ≤ 600 LOC
- [ ] 4.4 `grep -c "public.*void.*Test\|\[Fact\]" TypeCheckerTests*.cs` 总和不变

### 阶段 5: 文档同步
- [ ] 5.1 `docs/review.md` 路线图 `split-large-test-files` 状态注记 C# 部分已完成；rc_heap 部分留 `split-rc-heap-tests` 后续

### 阶段 6: 归档 + 提交
- [ ] 6.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 6.2 `docs/spec/changes/split-typechecker-tests/` → `docs/spec/archive/2026-05-07-split-typechecker-tests/`
- [ ] 6.3 commit + push

---

## 备注

- **零行为变化**: 无代码逻辑修改，仅搬运
- **测试要求**（refactor 类型）: "确保已有测试仍覆盖；不得删除测试"——无新增
- **Git 识别 rename**: 因为是搬运 + partial 拆分（不是简单 mv），git 大概率不会自动识别为 rename。这是预期的——diff 体积稍大，但每个 hunk 都是机械性搬运
- **后续**: `split-rc-heap-tests` 独立 spec 处理 Rust 端 1229 LOC 文件
