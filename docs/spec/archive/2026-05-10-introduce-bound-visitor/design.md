# Design: Bound Tree Visitor Framework

## Architecture

```
BoundExpr (abstract record)
   ├── BoundLitInt, BoundLitFloat, ... (~25 leaves)
   └── ...

         ↓ accepted by

BoundExprVisitor<TResult>             (abstract; switch in Visit)
   ├── protected abstract VisitLitInt(BoundLitInt n)  : TResult
   ├── protected abstract VisitLitFloat(BoundLitFloat f) : TResult
   ├── ... (one per concrete BoundXxx)
   └── public TResult Visit(BoundExpr e) => e switch { ... };

BoundExprWalker : BoundExprVisitor<Unit>     (default impl: recurse children)
   ├── override of every leaf is a no-op
   ├── override of every interior recurses on its child BoundExpr / BoundStmt
   └── subclass overrides only the nodes it cares about

Same shape for BoundStmtVisitor<TResult> + BoundStmtWalker.

Concrete callers:
   IrEmitExprVisitor : BoundExprVisitor<TypedReg>     (in FunctionEmitter)
   IrEmitStmtVisitor : BoundStmtVisitor<Unit>         (in FunctionEmitter)
   ClassRefScanner   : BoundExprWalker                (CollectClassRefs)
   FlowAnalyzer      : uses BoundStmtWalker / BoundExprWalker
   ClosureEscapeAnalyzer : same
```

`Unit` = `record struct Unit;` placeholder so void-returning visitors can share `BoundExprVisitor<TResult>` without a separate base class. (.NET 类型规则不允许 `void` 作泛型参数。)

## Decisions

### Decision 1: Visitor 与 FunctionEmitter（partial sealed internal class）的耦合方式

**问题**：`EmitExpr` switch 内部访问大量 emitter 私有字段（`_locals` / `_ctx` / `_currentClassName` / `_envReg` / ...）和私有方法（`Emit` / `Alloc` / `EmitInterpolation` / ...）。把它移成顶层 visitor 类会强迫所有这些字段升级到 `internal` 或经构造函数注入，破坏封装。

**选项**：
- **A. Nested visitor class**（`FunctionEmitter.IrEmitExprVisitor`）—— C# nested class 能直接访问 enclosing class 的 instance 字段（通过传 `this` 引用），保持封装；缺点是 `FunctionEmitter` 代码量短期不下降
- **B. 独立 visitor 类 + 构造函数注入 `FunctionEmitter`**—— 解耦，但需要把 ~15 个私有字段/方法升级到 `internal` 才能让 visitor 调用，等于破坏现有封装
- **C. 把 visitor 写成 partial class 的扩展**（`FunctionEmitterEmitExprVisitor.cs` 作为 `partial class FunctionEmitter` 的一部分，其中定义 `private sealed class IrEmitExprVisitor : BoundExprVisitor<TypedReg>` nested）—— 等价于 A，文件物理拆分

**决定**：**A + C 组合**——`IrEmitExprVisitor` 作为 `FunctionEmitter` 的 `private sealed class` nested type，写在 `FunctionEmitterExprs.cs`（partial 文件）中。理由：
- 保持封装零破坏
- nested class 持有 `FunctionEmitter outer` 字段，所有 helper 调用变 `outer.Alloc(...)` / `outer.Emit(...)`——读起来比"this 隐式访问"更明确，反而提升可读性
- 一次到位，不需要为后续 `split-function-emitter` 妥协

### Decision 2: Walker（void 遍历）vs Visitor<TResult> 的关系

**问题**：CollectClassRefs / FlowAnalyzer / ClosureEscapeAnalyzer 都是"扫描 Bound 树，更新外部状态，无返回值"的 walker 形态。要么:
- 让它们继承 `BoundExprVisitor<Unit>` 自己手写"递归到子节点"的逻辑
- 提供 `BoundExprWalker` 默认实现，子类只 override 关心的节点

**决定**：**提供 `BoundExprWalker` 和 `BoundStmtWalker`** 默认实现。理由：
- "递归到子节点" 的逻辑每个 walker 都要写一遍，是 visitor 模式的高频套路
- Walker 的 default 实现集中在一处定义，新增 BoundExpr 节点时只改 walker 默认（递归子节点），子类不需要联动
- Roslyn / Clang 都同时提供 `Visitor<T>` + `Walker`（Clang `RecursiveASTVisitor`）；这是验证过的架构

### Decision 3: 每个具体 visitor 继承的 TResult

| Visitor | TResult | 说明 |
|---|---|---|
| `IrEmitExprVisitor` | `TypedReg` | 返回 emit 后的目标寄存器 |
| `IrEmitStmtVisitor` | `Unit` | 语句无返回值，但走 visitor 拿编译期 exhaustive 检查 |
| `ClassRefScanner` | `Unit` (via Walker) | 副作用写入外部 `HashSet<string>` |
| `FlowAnalyzer` 现有 3 处 | `bool` (always-returns) / `Unit` | 按各自语义 |
| `ClosureEscapeAnalyzer` 3 处 | `Unit` (via Walker) | 收集到外部状态 |

### Decision 4: Exhaustive 检查策略

**问题**：当前 switch 末尾用 `default → throw NotSupportedException`，新增 BoundXxx 节点忘改 dispatch → 运行期 ICE。Visitor 模式的核心收益就是把这个错误提前到编译期。

**实现**：
- `BoundExprVisitor<T>.Visit(BoundExpr e)` 基类用 `switch (e) { ... default: throw ICE }` 实现
- 但每个 `protected abstract VisitXxx` 强制子类必须 override（C# 编译期检查）
- 新增 BoundXxx 节点时，**步骤是先在 BoundExprVisitor 基类的 switch 中加一个 case + 加一个 abstract 方法**——这一步 build 后所有子类**编译期失败**（必须实现新 abstract），强制全员关注

**约束**：BoundExpr 抽象记录的层级要保持扁平（不要中间抽象），否则 visitor 接口会爆炸。当前代码已是扁平的，无需调整。

### Decision 5: 命名约定

- `BoundLitInt` → `VisitLitInt`（去掉"Bound"前缀，简洁）
- `BoundCall` → `VisitCall`
- 抽象 helper 节点（`BoundLambdaParam` / `BoundCapture` / `BoundCatchClause` / `BoundSwitchArm` / `BoundSwitchCase` / `BoundLambdaBody` 派生）：**不在 visitor 接口里**，由相关父节点的 visitor 方法自己处理（这些节点不是 BoundExpr / BoundStmt 的直接子类，独立辅助 record）

## Implementation Notes

### Migration order

按"风险递增 / 体量递增"顺序：

1. **基础设施**：`BoundExprVisitor.cs` + `BoundStmtVisitor.cs` + `BoundExprWalker` + `BoundStmtWalker`（NEW，纯定义）
2. **`CollectClassRefs`**（最小，纯 read，验证 walker 模式）
3. **`FlowAnalyzer`** 3 处（read-only，已有清晰边界）
4. **`ClosureEscapeAnalyzer`** 3 处（read-only + side-effect 收集）
5. **`EmitBoundStmt`**（mutation 重，但结构清晰）
6. **`EmitExpr`**（最大，~25 case，nested visitor 内访问 outer 字段最频繁）

每一步独立 commit；每一步 commit 前 `dotnet test` 全绿。

### 关键 API 形态预览

```csharp
// src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs
namespace Z42.Semantics.Bound;

public readonly record struct Unit;  // void-substitute for generic

public abstract class BoundExprVisitor<TResult>
{
    public TResult Visit(BoundExpr e) => e switch
    {
        BoundLitInt n            => VisitLitInt(n),
        BoundLitFloat f          => VisitLitFloat(f),
        BoundLitStr s            => VisitLitStr(s),
        BoundLitBool b           => VisitLitBool(b),
        BoundLitNull n           => VisitLitNull(n),
        BoundLitChar c           => VisitLitChar(c),
        BoundDefault d           => VisitDefault(d),
        BoundInterpolatedStr i   => VisitInterpolatedStr(i),
        BoundIdent id            => VisitIdent(id),
        BoundCapturedIdent ci    => VisitCapturedIdent(ci),
        BoundAssign a            => VisitAssign(a),
        BoundBinary b            => VisitBinary(b),
        BoundUnary u             => VisitUnary(u),
        BoundPostfix p           => VisitPostfix(p),
        BoundLambda l            => VisitLambda(l),
        BoundCall c              => VisitCall(c),
        BoundModifiedArg m       => VisitModifiedArg(m),
        BoundMember m            => VisitMember(m),
        BoundIndex i             => VisitIndex(i),
        BoundCast c              => VisitCast(c),
        BoundNew n               => VisitNew(n),
        BoundArrayCreate ac      => VisitArrayCreate(ac),
        BoundArrayLit al         => VisitArrayLit(al),
        BoundConditional c       => VisitConditional(c),
        BoundNullCoalesce n      => VisitNullCoalesce(n),
        BoundNullConditional n   => VisitNullConditional(n),
        BoundIsPattern ip        => VisitIsPattern(ip),
        BoundSwitchExpr s        => VisitSwitchExpr(s),
        BoundError err           => VisitError(err),
        _ => throw new InvalidOperationException(
            $"BoundExprVisitor: unhandled BoundExpr subtype {e.GetType().Name} (ICE — add to base switch)")
    };

    protected abstract TResult VisitLitInt(BoundLitInt n);
    protected abstract TResult VisitLitFloat(BoundLitFloat f);
    protected abstract TResult VisitLitStr(BoundLitStr s);
    protected abstract TResult VisitLitBool(BoundLitBool b);
    protected abstract TResult VisitLitNull(BoundLitNull n);
    protected abstract TResult VisitLitChar(BoundLitChar c);
    protected abstract TResult VisitDefault(BoundDefault d);
    protected abstract TResult VisitInterpolatedStr(BoundInterpolatedStr i);
    protected abstract TResult VisitIdent(BoundIdent id);
    protected abstract TResult VisitCapturedIdent(BoundCapturedIdent ci);
    protected abstract TResult VisitAssign(BoundAssign a);
    protected abstract TResult VisitBinary(BoundBinary b);
    protected abstract TResult VisitUnary(BoundUnary u);
    protected abstract TResult VisitPostfix(BoundPostfix p);
    protected abstract TResult VisitLambda(BoundLambda l);
    protected abstract TResult VisitCall(BoundCall c);
    protected abstract TResult VisitModifiedArg(BoundModifiedArg m);
    protected abstract TResult VisitMember(BoundMember m);
    protected abstract TResult VisitIndex(BoundIndex i);
    protected abstract TResult VisitCast(BoundCast c);
    protected abstract TResult VisitNew(BoundNew n);
    protected abstract TResult VisitArrayCreate(BoundArrayCreate ac);
    protected abstract TResult VisitArrayLit(BoundArrayLit al);
    protected abstract TResult VisitConditional(BoundConditional c);
    protected abstract TResult VisitNullCoalesce(BoundNullCoalesce n);
    protected abstract TResult VisitNullConditional(BoundNullConditional n);
    protected abstract TResult VisitIsPattern(BoundIsPattern ip);
    protected abstract TResult VisitSwitchExpr(BoundSwitchExpr s);
    protected abstract TResult VisitError(BoundError err);
}

public abstract class BoundExprWalker : BoundExprVisitor<Unit>
{
    // Default: recurse into all child BoundExpr / BoundStmt nodes.
    // Subclasses override only what they care about.
    protected override Unit VisitLitInt(BoundLitInt n) => default;
    protected override Unit VisitLitFloat(BoundLitFloat f) => default;
    // ... (all leaves are no-op default)

    protected override Unit VisitBinary(BoundBinary b)
    {
        Visit(b.Left);
        Visit(b.Right);
        return default;
    }

    protected override Unit VisitCall(BoundCall c)
    {
        if (c.Receiver != null) Visit(c.Receiver);
        foreach (var a in c.Args) Visit(a);
        return default;
    }

    // ... interior nodes recurse on children
}
```

### 风险与缓解

| 风险 | 缓解 |
|---|---|
| `IrEmitExprVisitor` nested class 让 `FunctionEmitterExprs.cs` 临时膨胀（visitor 的 27 个方法 + 原来的 helper） | 接受。`split-function-emitter` follow-up spec 会按**节点类别**（literals / binary / member / call / control-flow）拆 partial 文件。本 spec 不解决文件膨胀，只解决"dispatch 模式统一" |
| Walker 对大节点（`BoundCall.Args`、`BoundLambda` 嵌套 body）的 default 递归可能行为不符合某个具体 walker 子类 | 子类 override 该节点的 Visit 方法，自己控制递归。Walker default 只是常见情况的便利 |
| 性能回归（switch + 虚方法 vs 直接 switch） | JIT 已能内联简单 dispatch；Bound 树规模小（典型函数 ~10–100 节点），开销可忽略。GREEN 验证含编译时间对比即可，无需 micro-bench |
| Migration 中间状态：visitor 已就绪但 `EmitExpr` 还是老 switch | 中间 commit 完全可工作（visitor 是新代码，老 switch 不动）。每步 commit GREEN，可滚到任意中间点 |

## Testing Strategy

- **单元测试** [`BoundVisitorTests.cs`](../../../src/compiler/z42.Tests/BoundVisitorTests.cs) (NEW)：
  - `Visit_AllConcreteBoundExprTypes_DispatchesCorrectly`：用反射枚举 `Bound` 命名空间下所有 `BoundExpr` 派生类，构造测试 visitor 验证每种都有对应 `VisitXxx` 被调用
  - `Visit_AllConcreteBoundStmtTypes_DispatchesCorrectly`：同上
  - `Walker_DefaultBehavior_RecursesIntoChildren`：构造一棵 `BoundBinary(Lit, Lit)`，walker 计数器验证 3 次访问
- **回归覆盖**：现有 757 C# Tests + 106 VM golden 必须 100% 全绿（不允许任何回归）
- **行为不变性证明**：每步迁移后 `dotnet test` + `./scripts/test-vm.sh` 全绿即可——visitor 与 switch 的输出 IR 应字节相同

## Estimated Effort

约 1.5 天（每步独立 commit）：
- Visitor 基础设施（含测试）：0.3 天
- CollectClassRefs 迁移：0.1 天
- FlowAnalyzer 迁移：0.2 天
- ClosureEscapeAnalyzer 迁移：0.2 天
- EmitBoundStmt 迁移：0.3 天
- EmitExpr 迁移（最大）：0.4 天
