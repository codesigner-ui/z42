# Design: L3 闭包核心 — 捕获 + 档 C 堆擦除

## Architecture

```
源码 `var k=10; var f = () => k;`
  ↓ [Parser] 与 L2 一致：LambdaExpr 字面量
  ↓ [TypeChecker]
       BindLambda 进入 lambda binding context（push frame { OuterEnv=env, Captures=[] }）
       BindIdent("k", lambdaEnv):
         - varType = lambdaEnv.LookupVar("k") = int
         - boundary check：k 在 boundary 之上 → capture
         - 不报错；append BoundCapture(k, int, ValueSnapshot) 到 frame.Captures
         - 返回 BoundCapturedIdent(k, int, CaptureIndex=0)
       BindLambda 返回 BoundLambda { Captures=[k], Body=... }
  ↓ [IrGen] EmitLambdaLiteral(BoundLambda):
       if Captures.Count > 0:
         lifted = EmitLiftedWithEnv(name, body)
         emit MkClos { dst, fn_name, capture_regs = [reg("k")] }
       else:
         (保留 L2 路径) emit LoadFn
       lifted body 内 BoundCapturedIdent("k", idx=0) → ArrayGet(dst, env_reg=0, const(0))
  ↓ [VM]
       Instruction::MkClos { dst, fn_name, capture_regs }:
         env_vec = capture_regs 对应值的拷贝
         heap_alloc env_vec → GcRef
         dst ← Value::Closure { env, fn_name }
       Instruction::CallIndirect callee, args:
         match callee:
           Value::FuncRef(name) → exec_function(name, args)
           Value::Closure { env, fn_name } → exec_function(fn_name, [env] ++ args)
```

## Decisions

### Decision 1: env 表示 — Vec<Value> vs ScriptObject
**问题**：env 是堆上的什么对象？
**选项**：A `Vec<Value>`（数组式，索引访问）；B 合成 ScriptObject（带类描述符，字段访问）
**决定**：**A `Vec<Value>`**（即 `Value::Array(GcRef<Vec<Value>>)`）。
**理由**：最简单，复用现有 Array GC 路径；ZBC 不需要新增"合成 class 描述符"的序列化通路；slot 索引访问通过现有 ArrayGet/Set 即可。

### Decision 2: 闭包 Value variant
**决定**：新增 `Value::Closure { env: GcRef<Vec<Value>>, fn_name: String }`。
保留 `Value::FuncRef(String)` 用于无捕获情况（避免 alloc 开销）。

### Decision 3: BoundLambda 内嵌 Captures
**决定**：
```csharp
public sealed record BoundLambda(
    IReadOnlyList<BoundLambdaParam> Params,
    BoundLambdaBody Body,
    Z42FuncType FuncType,
    IReadOnlyList<BoundCapture> Captures,    // NEW
    Span Span) : BoundExpr(FuncType, Span);

public enum BoundCaptureKind { ValueSnapshot, ReferenceShare }

public sealed record BoundCapture(string Name, Z42Type Type, BoundCaptureKind Kind, Span Span);

public sealed record BoundCapturedIdent(
    string Name, Z42Type Type, int CaptureIndex, Span Span) : BoundExpr(Type, Span);
```

### Decision 4: TypeChecker 的 lambda binding 栈
**决定**：把 `_lambdaOuterStack: Stack<TypeEnv>` 替换为：

```csharp
private sealed class LambdaBindingFrame
{
    public TypeEnv OuterEnv { get; init; } = null!;
    public List<BoundCapture> Captures { get; } = new();
    public Dictionary<string, int> NameToIndex { get; } = new();   // dedup
}

private readonly Stack<LambdaBindingFrame> _lambdaBindingStack = new();
```

进入 lambda body 前 push frame，BindIdent 用 frame 收集 captures（dedup by name），出 lambda 时 pop 并把 frame.Captures 写入 BoundLambda.Captures。

### Decision 5: BindIdent capture 路径
```csharp
var varType = env.LookupVar(id.Name);
if (varType != null)
{
    if (_lambdaBindingStack.Count > 0)
    {
        var frame = _lambdaBindingStack.Peek();
        if (!env.ResolvesVarBelowBoundary(id.Name, frame.OuterEnv))
        {
            // It's a capture. Dedup + register.
            if (!frame.NameToIndex.TryGetValue(id.Name, out var idx))
            {
                idx = frame.Captures.Count;
                var kind = Z42Type.IsReferenceType(varType)
                    ? BoundCaptureKind.ReferenceShare
                    : BoundCaptureKind.ValueSnapshot;
                frame.Captures.Add(new BoundCapture(id.Name, varType, kind, id.Span));
                frame.NameToIndex[id.Name] = idx;
            }
            return new BoundCapturedIdent(id.Name, varType, idx, id.Span);
        }
    }
    return new BoundIdent(id.Name, varType, id.Span);
}
```

### Decision 6: 同名嵌套 capture 实现
**问题**：lambda f 内的 lambda g 都引用最外层 `k`。
**决定**：g 的 captures 列表也包含 k；其值通过 f 的 env 取得。

**实现**：递归。当生成 g 的 MkClos 时（在 f 的 lifted body 内），g 的 capture_regs[i] 通过 f 的 env ArrayGet 取得。每层 lambda 的 captures 都解析为父层的 BoundCapturedIdent → 链式 ArrayGet。**无需特殊处理**。

具体路径：
- BoundCapturedIdent("k", g_idx) on g 自身的 env（在 g 的 lifted body 内）
- 生成 g 的 MkClos 时（在 f 的 lifted body 内），需要为 g 提供 capture_regs
- capture_regs 来自当前 emitter scope 中"k"的解析 = BoundCapturedIdent("k", f_idx)
- IrGen 在 f 的 lifted body 内 emit `ArrayGet(env_reg=0, f_idx)` 作为 g 的 capture_regs[g_idx]

### Decision 7: IR `MkClos` 编码
```csharp
public sealed record MkClosInstr(
    TypedReg Dst, string FuncName, List<TypedReg> Captures) : IrInstr;
```
**Opcode**：`MkClos = 0x57`（在 0x55=LoadFn, 0x56=CallIndirect 之后）。
**ZBC 编码**：
```
[opcode 1][type_tag 1][dst 2][fn_idx 4][num_captures 2][cap_reg 2 * N]
```

### Decision 8: 无捕获保留 LoadFn
**决定**：当 `BoundLambda.Captures` 为空时，IrGen 仍发 `LoadFn`（不退化为 MkClos with 0 captures）。
**理由**：避免不必要的 heap alloc；保持 L2 lambda_l2_basic 端到端行为不变。

### Decision 9: Local function 同样路径
**决定**：local fn 现有"L2 一层嵌套限制"中"不允许 capture"分支移除。捕获非空时，BindLocalFunctionStmt 同 BindLambda 走 capture 路径。
- 一层嵌套限制（不允许 local fn 内嵌 local fn）保留
- TypeCheck 阶段：lift to BoundLocalFunction with Captures
- IrGen 阶段：lifted local fn 加 env param；调用从 `Call <lifted>` 改为 `MkClos + CallIndirect`

无 capture 时仍走原 `Call <lifted>` 路径（避免对 impl-local-fn-l2 既有行为造成回归）。

### Decision 10: VM Closure dispatch
```rust
Instruction::CallIndirect { dst, callee, args } => {
    match frame.get(*callee)? {
        Value::FuncRef(name) => {
            let arg_vals = collect_args(&frame.regs, args)?;
            exec_function(ctx, module, lookup(name), &arg_vals)?
        }
        Value::Closure { env, fn_name } => {
            let mut all_args = vec![Value::Array(env.clone())];
            all_args.extend(collect_args(&frame.regs, args)?);
            exec_function(ctx, module, lookup(fn_name), &all_args)?
        }
        other => bail!("CallIndirect: expected FuncRef or Closure, got {:?}", other),
    }
}
```

## Implementation Notes

### TypeChecker capture binding（详细）

```csharp
private BoundExpr BindLambda(LambdaExpr lambda, TypeEnv env, Z42Type? expected)
{
    // ... existing param/expected resolution ...

    var lambdaEnv = env.PushScope();
    foreach (var bp in boundParams) lambdaEnv.Define(bp.Name, bp.Type);

    var frame = new LambdaBindingFrame { OuterEnv = env };
    _lambdaBindingStack.Push(frame);
    BoundLambdaBody body;
    Z42Type retType;
    try
    {
        // ... existing body bind ...
    }
    finally { _lambdaBindingStack.Pop(); }

    var fnType = new Z42FuncType(paramTypes, retType);
    return new BoundLambda(boundParams, body, fnType, frame.Captures, lambda.Span);
}
```

### BindLocalFunctionStmt 同样适配

`impl-local-fn-l2` 当时的"嵌套报错"原意是禁止 local fn 内嵌 local fn（L2 限制）—— 本变更不放开此限制。但 local fn 体内引用外层 local 现在是 capture（合法）。

### IrGen 关键改动

**EmitLambdaLiteral 分叉**：

```csharp
private TypedReg EmitLambdaLiteral(BoundLambda lambda)
{
    var index    = _ctx.NextLambdaIndex(_currentFnQualName);
    var liftedNm = $"{_currentFnQualName}__lambda_{index}";

    if (lambda.Captures.Count == 0)
    {
        // L2 path (unchanged)
        var lifted = new FunctionEmitter(_ctx).EmitLifted(liftedNm, lambda);
        _ctx.RegisterLiftedFunction(lifted);
        var dst = Alloc(IrType.Ref);
        Emit(new LoadFnInstr(dst, liftedNm));
        return dst;
    }

    // L3 capture path
    var captureRegs = lambda.Captures
        .Select(c => EmitCaptureExpr(c))
        .ToList();
    var lifted2 = new FunctionEmitter(_ctx).EmitLiftedWithEnv(liftedNm, lambda);
    _ctx.RegisterLiftedFunction(lifted2);
    var dst2 = Alloc(IrType.Ref);
    Emit(new MkClosInstr(dst2, liftedNm, captureRegs));
    return dst2;
}
```

`EmitCaptureExpr(BoundCapture c)` 返回 capture 名字在当前 emitter 范围内的 reg：
- 如果当前是 outer 函数，且 c 是普通 local → `_locals[c.Name]`
- 如果当前是 lifted body（即嵌套 lambda 创建子 lambda 时），c 应该是 outer 闭包的 capture → 通过当前 env_reg 的 ArrayGet 读取

**EmitLiftedWithEnv** 是 EmitLifted 的 capturing 变体：
- 第 0 寄存器 reserve 为 env（IrType.Ref，自动加入参数计数）
- 用户参数从 reg 1 开始
- BoundCapturedIdent → ArrayGet(env_reg=0, idx)

### `Value::Closure` 的 GC 处理

```rust
// rc_heap.rs object_size_bytes:
Value::Closure { env, fn_name } =>
    size_of::<Value>() + env.borrow().capacity() * size_of::<Value>() + fn_name.capacity()
// scan_object_refs: env 内的 Object refs 通过现有 Array 路径递归扫描
```

### VM `Instruction::MkClos`

```rust
Instruction::MkClos { dst, fn_name, captures } => {
    let mut env_vec = Vec::with_capacity(captures.len());
    for r in captures {
        env_vec.push(frame.get(*r)?.clone());
    }
    let env_ref = ctx.heap().alloc_array(env_vec);
    let env = match env_ref { Value::Array(rc) => rc, _ => unreachable!() };
    frame.set(*dst, Value::Closure { env, fn_name: fn_name.clone() });
}
```

## Testing Strategy

### 单元测试矩阵

| Requirement | 测试类型 | 文件 |
|---|---|---|
| L3-C-1 capture 分析 | TypeCheck unit | `ClosureCaptureTypeCheckTests.cs` |
| L3-C-2 闭包对象表示 | IrGen + VM golden | snapshot + golden |
| L3-C-3 值快照 | golden run | `closure_l3_capture/value_snapshot/` |
| L3-C-4 引用身份 | golden run | `closure_l3_capture/ref_share/` |
| L3-C-5 MkClos IR | IrGen snapshot | `ClosureCaptureIrGenTests.cs` |
| L3-C-6 VM Closure 调用 | golden run | `closure_l3_capture/basic_call/` |
| L3-C-7 lifted env param | IrGen snapshot | `ClosureCaptureIrGenTests.cs` |
| L3-C-8 closure.md 调整 | manual review | docs |
| L3-C-9 同名嵌套 | golden run | `closure_l3_capture/nested/` |
| L3-C-10 local fn capture | golden run | `closure_l3_capture/local_fn/` |
| L3-C-11 spawn 不强制 Send | TypeCheck unit | confirms no Z0809 |

### Golden 路径

```
src/runtime/tests/golden/run/closure_l3_capture/
├── source.z42         # 综合：value/ref/nested/local-fn 全覆盖
├── expected_output.txt
├── source.zbc / source.z42ir.json
└── interp_only        # MkClos JIT 待 C4
```

### 验证命令（GREEN 标准）

`dotnet build` + `cargo build` + `dotnet test`（100%）+ `./scripts/test-vm.sh`（100%）。

## Risk & Open Items

| 风险 | 缓解 |
|------|------|
| 嵌套 lambda capture 实现复杂（Decision 6）| 通过递归 IrGen 自然 — 子 lambda 的 capture_regs 通过父 lambda env 的 ArrayGet 取得；测试覆盖 |
| `_lambdaOuterStack` → `_lambdaBindingStack` 改造涉及 lambda + local fn 共用栈 | 重命名 + 替换数据结构，搜索全 codebase 5 处引用，改动控制在 30 行内 |
| `Value::Closure` 加入破坏现有 Value match 穷尽性 | rust 编译器强制全覆盖；改 ToString / size / scan / convert 等所有 Value match 处（已知 ~6 处） |
| MkClos 的 ZBC 序列化 round-trip | 复用 BinaryFormat 现有 args 序列化模式（与 CallIndirect 一致）|
| `Func<T,R>` 类型推断在 capture 后是否仍正确 | BoundLambda.FuncType 在 BindLambda 末尾才合成，不受 captures 影响 |
| 同一 lambda 多次创建产生多个 env？是的，每次 MkClos 是新分配 | 设计正确（不同上下文不同 env）；与 R7 循环变量新绑定（C3 工作）配合 |
