# Tasks: split-function-emitter-exprs

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 1 §1.1 P0 残留（4 个超 500 LOC 文件之一，最大）

## 验证报告

### 编译状态
- ✅ `dotnet build src/compiler/z42.slnx`: 0 Warning / 0 Error

### 测试结果
- ✅ `dotnet test`: **1104/1104** 全绿
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**

### LOC 目标
- 主 FunctionEmitterExprs.cs: 878 → **274**（仅 EmitExpr dispatcher）
- 5 个 partial 文件全部 ≤ 300 软限：
  - Lambdas.cs: 221
  - Members.cs: 152
  - Binary.cs: 97
  - Conditional.cs: 95
  - Operators.cs: 88
- 总和 927 LOC（vs 原 878）—— 多出来的是每个 partial 文件的 using/namespace/class wrapper

### 结论：✅ 全绿，可归档

**变更说明**: 把 `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` (878 LOC) 按现有 `// ── XXX` section 头 + helper 方法主题分组拆为 1 主文件 + 5 个 partial 子文件，每个 ≤ 300 LOC（[code-organization.md](../../../.claude/rules/code-organization.md) 软限）。

**原因**: 单文件 878 LOC 严重超 500 硬限。FunctionEmitter 已是 partial class（4 个文件），添加更细的 partial 文件按主题分组与现有结构一致。**纯方法搬运 + namespace/using 同步**，零代码逻辑变化、零行为变化。

**与 deferred D-11 的关系**: 本 spec **不做** introduce-bound-visitor 的 method extraction（不抽 inline case 体为 helper）——主 EmitExpr dispatcher 保持原状，仅按现有 helper 方法边界拆文件。visitor 抽象基类引入待 D-11 触发条件成熟时另立 spec。

**文档影响**:
- `docs/review.md` — 路线图 Part 1 §1.1 P0 残留之一拆分完成（FunctionEmitterExprs.cs）；其余 3 个 (`IrGen.cs` / `ImportedSymbolLoader.cs` / `TypeChecker.Calls.cs`) 留独立 spec
- `src/compiler/z42.Semantics/Codegen/` — 无 README（不在强制 README 范围内：3 层目录已含 z42.Semantics/）

---

## Scope（允许改动的文件）

### MODIFY

| 文件 | 说明 |
|---|---|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | 保留 line 1-274（主 EmitExpr dispatcher），删除 line 275-878 已拆出的 helper 方法 |
| `docs/review.md` | 路线图状态注记 |

### NEW (5 partial files)

| 文件 | 涵盖原行号 | 涵盖方法 | 估计 LOC |
|---|---|---|---|
| `FunctionEmitterExprs.Lambdas.cs` | 275-484 | `EmitLambdaLiteral` / `EmitCaptureExpr` / `EmitLiftedLambdaWithEnv` / `EmitLiftedLocalFunctionWithEnv` / `EmitLiftedLocalFunction` / `EmitLifted` | ~210 |
| `FunctionEmitterExprs.Members.cs` | 486-597 + 849-878 | `EmitBoundMember` / `IsInstanceMethodOf` / `EmitBoundAssign` / `EmitBoundNew` | ~143 |
| `FunctionEmitterExprs.Operators.cs` | 599-675 | `EmitBoundUnary` / `EmitBoundPostfix` | ~77 |
| `FunctionEmitterExprs.Conditional.cs` | 677-760 | `EmitBoundTernary` / `EmitBoundNullCoalesce` / `EmitBoundNullConditional` | ~84 |
| `FunctionEmitterExprs.Binary.cs` | 762-847 | `EmitBoundBinary` / `EmitShortCircuit` | ~86 |

每个 partial 文件用相同模板:
```csharp
using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

internal sealed partial class FunctionEmitter
{
    // 搬运的 method（保持私有访问性 / signature 不变）
}
```

**只读引用**:
- `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs` — 主 partial（fields / 构造器）
- `src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs` — 调用本文件 helper 的姐妹 partial
- `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` — 同上
- `src/compiler/z42.Semantics/Bound/BoundExpr.cs` — Bound 节点定义

---

## 任务清单

### 阶段 1: 准备
- [ ] 1.1 baseline: `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 1104/1104 全绿
- [ ] 1.2 行号边界已确认：grep 输出与 Scope 表对照（已完成）

### 阶段 2: 创建 5 个 partial 文件
按主题搬运（不改方法签名 / 不改实现 / 不改访问性）:
- [ ] 2.1 `FunctionEmitterExprs.Lambdas.cs` — 6 个 lambda/closure helper
- [ ] 2.2 `FunctionEmitterExprs.Members.cs` — 4 个 member/assign/new helper
- [ ] 2.3 `FunctionEmitterExprs.Operators.cs` — 2 个 unary/postfix helper
- [ ] 2.4 `FunctionEmitterExprs.Conditional.cs` — 3 个 ternary/null helper
- [ ] 2.5 `FunctionEmitterExprs.Binary.cs` — 2 个 binary helper

### 阶段 3: 改造主文件
- [ ] 3.1 删除已搬出的方法（保留 line 1-274 主 dispatcher + 顶部 helpers）
- [ ] 3.2 主文件 LOC 验证 ≤ 300 LOC

### 阶段 4: 验证
- [ ] 4.1 `dotnet build src/compiler/z42.slnx` 无 warning
- [ ] 4.2 `dotnet test`: **1104/1104** 不变
- [ ] 4.3 `./scripts/test-vm.sh`: 全绿（IR 输出不变 → VM 行为不变）
- [ ] 4.4 `wc -l src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs*.cs` 全部 ≤ 300 LOC

### 阶段 5: 文档同步
- [ ] 5.1 `docs/review.md` 路线图注记 FunctionEmitterExprs.cs 拆分完成；其余 3 个 P0 文件 (`IrGen.cs` / `ImportedSymbolLoader.cs` / `TypeChecker.Calls.cs`) 待跟进

### 阶段 6: 归档 + 提交
- [ ] 6.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 6.2 `spec/changes/split-function-emitter-exprs/` → `spec/archive/2026-05-07-split-function-emitter-exprs/`
- [ ] 6.3 commit + push

---

## 备注

- **零行为变化**: 所有方法签名、实现、访问性保持不变
- **测试要求**（refactor 类型）: "确保已有测试仍覆盖；不得删除测试"——本 spec 不新增测试
- **后续 P0 残留**:
  - `split-irgen` — IrGen.cs (806 LOC)
  - `split-imported-symbol-loader` — ImportedSymbolLoader.cs (730 LOC)
  - `split-typechecker-calls` — TypeChecker.Calls.cs (686 LOC)
- **D-11 关联**: 本 spec 不做 inline case 抽 method（visitor pattern）；主 dispatcher 保留 274 LOC 含所有 inline case 体。D-11 触发条件成熟时再拆这部分。
