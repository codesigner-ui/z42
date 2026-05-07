# Tasks: split-irgen

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 1 §1.1 P0 残留（4 个超 500 LOC 文件之一，2/4）

## 验证报告

### 编译状态
- ✅ `dotnet build src/compiler/z42.slnx`: 0 Warning / 0 Error

### 测试结果
- ✅ `dotnet test`: **1104/1104**
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**

### LOC 目标
- 主 IrGen.cs: 806 → **198**
- 5 partial 全部 ≤ 250：Generate.cs 236 / Functions.cs 134 / Classes.cs 122 / Tests.cs 117 / Helpers.cs 59

### 结论：✅ 全绿，可归档

**变更说明**: 把 `src/compiler/z42.Semantics/Codegen/IrGen.cs` (806 LOC) 改为 `public sealed partial class IrGen`，按现有 `// ── XXX` section 头拆为 1 主文件 + 5 个 partial 子文件。每个 ≤ 250 LOC。

**原因**: 单文件 806 LOC 超 500 硬限。各功能区块（Generate 主入口 / Test discovery / Per-function delegation / Class descriptors / Module helpers）天然解耦，按主题拆有助导航。**纯方法搬运 + class 改 partial**，零行为变化。

**文档影响**:
- `docs/review.md` — 路线图状态注记 P0 残留 2/4 落地

---

## Scope（允许改动的文件）

### MODIFY
| 文件 | 说明 |
|---|---|
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | line 17 `public sealed class IrGen` → `public sealed partial class IrGen`；保留 line 1-197（fields + IEmitterContext + ctor）；删除已搬出方法 |
| `docs/review.md` | 路线图状态注记 |

### NEW (5 partial files)
| 文件 | 涵盖原行号 | 涵盖方法 / 块 | 估计 LOC |
|---|---|---|---|
| `IrGen.Generate.cs` | 199-421 | `Generate(CompilationUnit cu)` 主入口（test/class/function emission orchestration） | ~225 |
| `IrGen.Tests.cs` | 423-526 | `BuildTestEntry` / `BuildShouldThrowChain` / `IsDescendantOf`（test discovery helpers） | ~110 |
| `IrGen.Functions.cs` | 528-648 | `EmitMethod` / `EmitImplicitCtor` / `EmitFunction` / `GetBoundBody`（per-function delegation） | ~127 |
| `IrGen.Classes.cs` | 650-758 | `ClassIrShortName` / `EmitClassDesc` / `EmitNativeStub`（class descriptors） | ~115 |
| `IrGen.Helpers.cs` | 760-805 | `QualifyName` / `Intern` / `TypeName`（module-level helpers） | ~52 |

每个 partial 文件用相同模板:
```csharp
using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

public sealed partial class IrGen
{
    // 搬运的方法（保持访问性 / signature 不变）
}
```

**只读引用**:
- `src/compiler/z42.Semantics/Codegen/IEmitterContext.cs` — interface 定义
- `src/compiler/z42.Semantics/Codegen/FunctionEmitter*.cs` — Per-function emission delegate

---

## 任务清单

### 阶段 1: 准备
- [ ] 1.1 baseline: `dotnet test`: 1104/1104 全绿
- [ ] 1.2 行号边界已确认（已完成）

### 阶段 2: 创建 5 个 partial 文件
- [ ] 2.1 `IrGen.Generate.cs` — Generate 主方法
- [ ] 2.2 `IrGen.Tests.cs` — Test discovery helpers
- [ ] 2.3 `IrGen.Functions.cs` — Per-function emission
- [ ] 2.4 `IrGen.Classes.cs` — Class descriptors + native stub
- [ ] 2.5 `IrGen.Helpers.cs` — Module-level helpers

### 阶段 3: 改造主文件
- [ ] 3.1 `class IrGen` → `partial class IrGen`
- [ ] 3.2 删除已搬出的方法（保留 line 1-197）
- [ ] 3.3 主文件 LOC 验证 ≤ 250

### 阶段 4: 验证
- [ ] 4.1 `dotnet build` 0 warning / 0 error
- [ ] 4.2 `dotnet test`: 1104/1104 不变
- [ ] 4.3 `./scripts/test-vm.sh`: 全绿（IR 输出不变）
- [ ] 4.4 `wc -l IrGen*.cs` 全部 ≤ 250 LOC

### 阶段 5: 文档同步
- [ ] 5.1 `docs/review.md` 路线图注记

### 阶段 6: 归档 + 提交
- [ ] 6.1 状态 🟡 → 🟢
- [ ] 6.2 archive
- [ ] 6.3 commit + push

---

## 备注

- **零行为变化**: 所有方法签名、实现、访问性保持不变
- **partial class 一致 modifier**: 每个文件用 `public sealed partial class IrGen`（C# 要求一致）
- **后续 P0 残留**: split-imported-symbol-loader (730) / split-typechecker-calls (686)
