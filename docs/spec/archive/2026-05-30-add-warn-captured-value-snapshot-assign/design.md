# Design: W0604 — captured value-snapshot assign warning

## Architecture

```
TypeChecker.BindAssign (existing)
  ├─ recognize event += /−= desugar
  ├─ recognize indexer set_Item dispatch
  ├─ recognize property set_X dispatch
  ├─ bind target + value, type-check assignment
  ├─ remove alias (closure monomorphize bookkeeping)
  ├─ check `in` parameter writeback (E0xxx — pre-existing)
  ↓
  ★ NEW: if (target is BoundCapturedIdent ci
              && !Z42Type.IsReferenceType(ci.Type)
              && !IsCompilerSynthesised(assign.Span))
       _diags.Warning(CapturedValueSnapshotAssign, …, assign.Span);
  ↓
  return new BoundAssign(target, value2, value2.Type, assign.Span);
```

## Decisions

### Decision 1: W0604 触发条件 = `BoundCapturedIdent` 命中 + value type

**问题**：用什么信号判定"这是对值快照 capture 的写入"？

**选项：**
- A — target 是 `BoundCapturedIdent` + `target.Type` 非引用类型
- B — 走 capture frame，查 `BoundCapture.Kind == ValueSnapshot`
- C — 后处理 pass 在 BoundAssign 之上分析

**决定**：A。

**理由**：BoundCapturedIdent 在 BindAssign 时已经 bind 出来；`ci.Type` 就是 capture
的类型；与 TypeChecker.Exprs.cs:410-412 决定 `BoundCaptureKind` 用的
完全相同的判据 `Z42Type.IsReferenceType(varType)`。无需 lift capture
kind 进 `BoundCapturedIdent` shape；不需要新的 IR pass。改动面 minimal。

### Decision 2: warning vs error

**问题**：W vs E？

**选项：**
- A — Warning（W0604）— closure.md §4.1 是合法语义，但易踩
- B — Error（E0xxx）— 强制改写

**决定**：A。

**理由**：spec §4.1 明确"值类型按快照捕获"是 design feature，不是 bug。
有人可能确实想"在 closure 内修改局部副本而不传出"（罕见但合法）。
Warning 是正确严重度——明示常见用法应该改，但不阻塞编译。与 W0603
ReservedNamespace（合法但风险）的严重度档位一致。

### Decision 3: message 形式

模板：

```
warning W0604: assignment to captured value-type variable `<name>` is
local to the closure; outer scope keeps its original value. To share
mutable state across the closure boundary, wrap `<name>` in a class
(see docs/design/language/closure.md §4.4) or use a single-element
array as a cell, e.g. `bool[] <name> = new bool[1]; <name>[0] = ...`.
```

包含：

- 变量名（具体）
- 为什么会丢（local to the closure）
- 怎么修（class wrap 或 array cell）
- spec 引用（closure.md §4.4）

### Decision 4: 编译器生成的 sugar / desugar 不触发

某些 desugar（如 `+=` 之于 event）会在 BindAssign 内部递归 emit
BoundAssign-shaped node，可能误触发。检查 BindAssign 顶部已有的事件
desugar / indexer / property setter 分支：这些都 return early 不到末尾的
`new BoundAssign(...)`。剩下落到通用 `BoundAssign` 构造的路径**就是**
用户写的"真"赋值，正是要检查的对象。无需额外 sugar 标记。

### Decision 5: stdlib 现状扫描

实施前 / 后 run 一次 stdlib 全包 build，统计 W0604 真实出现次数。
预期：少量真实 hits（dead-import 一波类似），按 case 改写或加 suppression
注释（如果 spec §4.1 显式期望"local"语义，可加 `// W0604-suppress-intentional`
风格 comment）。

但 suppress 机制本身在本 spec out-of-scope；先观察次数，如多则单开
`add-w0604-suppress` follow-up。

## Implementation Notes

### Code locations

**`src/compiler/z42.Core/Diagnostics/Diagnostic.cs`**：紧贴 W0603 加一行

```csharp
public const string ReservedNamespace               = "W0603";
public const string CapturedValueSnapshotAssign     = "W0604";  // NEW
```

**`src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs`**：
BindAssign 末尾 `return new BoundAssign(...)` 之前插入

```csharp
if (target is BoundCapturedIdent ci
    && !Z42Type.IsReferenceType(ci.Type))
{
    _diags.Warning(
        DiagnosticCodes.CapturedValueSnapshotAssign,
        $"assignment to captured value-type variable `{ci.Name}` is " +
        "local to the closure; outer scope keeps its original value. " +
        "To share mutable state across the closure boundary, wrap in a " +
        "class (see docs/design/language/closure.md §4.4) or use a " +
        "single-element array as a cell.",
        assign.Span);
}
```

`_diags.Warning` 已在 DiagnosticBag 提供（line 26）。

### Test fixture

xUnit test mirror 既有 `WarnUnresolvedUsingsTests.cs` 结构：

```csharp
public class WarnCapturedValueSnapshotAssignTests
{
    [Fact] public void Bool_Capture_Assign_Warns()
    {
        var diags = CompileAndGetDiagnostics(@"
            void F() {
                bool x = false;
                Action a = () => { x = true; };
                a();
            }
        ");
        Assert.Contains(diags, d => d.Code == "W0604" && d.Message.Contains("`x`"));
    }
    // ... 5 more scenarios
}
```

`CompileAndGetDiagnostics` helper 已在 z42.Tests 内（mimic existing
diagnostic tests）。

### Stdlib scan

`./scripts/build-stdlib.sh 2>&1 | grep "W0604"` 实施前 / 后比对。如果
任何 stdlib 文件命中：

- 真正是"想跨闭包共享"误用 → fix（改 class / array cell）
- intentional local-effect → 加注释解释，等 future suppression 机制

每个 hit 列入实施备注。

## Testing Strategy

### xUnit (`z42.Tests/WarnCapturedValueSnapshotAssignTests.cs`)

6 scenarios from spec.md：

1. `Bool_Capture_Assign_Warns`
2. `Int_Compound_Capture_Assign_Warns`
3. `Reference_Type_Field_Write_NoWarn`（写 captured Counter 的 field）
4. `Array_Cell_Element_Write_NoWarn`（写 captured `bool[]` 的 [0]）
5. `Lambda_Local_Var_NoWarn`
6. `Top_Level_Function_Var_NoWarn`
7. `Nested_Lambda_Inner_Capture_Warns`

### Stdlib full-build

实施期：
- pre-impl baseline: `./scripts/build-stdlib.sh 2>&1 | grep -c "W0604"`
- post-impl: 同上
- 若 post > 0，逐条审视真伪

### GREEN

- `dotnet build src/compiler/z42.slnx`
- `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 含新增 xUnit + 既有不回归
- `./scripts/build-stdlib.sh` — stdlib 整体通过（warning 不阻塞）
- `./scripts/test-all.sh` — 全套 6 stages

### Pre-existing 回归

- 现 W0603 unused-import 警告路径不受影响
- closure 现有测试（lambda / nested / capture）all green
- 既有 stdlib：warning 数从 0 涨到 N — 验证 N 个全是真实可改写的 capture
  case，不存在 spurious hits（intentional 留 suppression follow-up）
