# Design: 闭包单态化（Closure Monomorphization）

## Architecture

```
┌────────────── 编译期（C#） ──────────────────┐
│                                              │
│  Parser ──► AST                              │
│              │                               │
│              ▼                               │
│  TypeChecker                                 │
│   ├─ BindVarDecl                             │
│   │    └─ 检测 init 是 BoundFuncRef /        │
│   │       BoundLambda → 记入                 │
│   │       _localFuncAliases                  │
│   ├─ BindIdent                               │
│   │    └─ 查 _localFuncAliases →             │
│   │       BoundIdent.ResolvedFuncName       │
│   └─ 跟踪重赋值 → 清掉 alias                 │
│              │                               │
│              ▼                               │
│  Codegen / FunctionEmitter                   │
│   └─ EmitBoundCall                           │
│        ├─ ResolvedFuncName != null           │
│        │   └─ Emit Call(name, args) ✓        │
│        └─ else                               │
│             └─ Emit CallIndirect(reg, args)  │
└──────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 分析层级 — TypeChecker vs Codegen

**问题**：单态化决策在哪里做？

**选项**：
- A: TypeChecker 阶段（扩展 BoundIdent.ResolvedFuncName）
- B: Codegen 阶段（实时跟踪 reg → resolved name）
- C: 单独的 `MonoPass` IR-level pass

**决定**：**选 A**。理由：

1. TypeChecker 已经做了符号解析（`_localFuncs` / `_localFnLiftedNames` 已存在）—— 顺手记录 alias 链是低成本扩展
2. BoundExpr 是不可变 record，新增字段不破坏现有消费者
3. Codegen 已经在用 BoundIdent.Capture / BoundCapturedIdent 做分支决策；多看一个字段是同模式扩展
4. 选项 C 增加一个 pass，但 IR 已经丢失"local var 是 alias"的信息（reg-only），等于重新做语义分析 → 不划算

### Decision 2: alias 跟踪范围 — 单赋值 vs 完整 dataflow

**问题**：哪些情形要识别为"已解析"？

**选项**：
- A: **仅单赋值**：`var f = Helper;` 命中；`f = OtherFunc;` 后所有 `f()` 退化为 CallIndirect
- B: **流敏感**：`if (cond) { f = A; } else { f = B; } f();` 也能识别为"二选一"
- C: **完整 SSA**：所有控制流分支单态化为不同 `Call` 指令

**决定**：**选 A**。理由：

1. 简单单赋值是最常见模式，覆盖 80%+ 实际用例
2. B / C 引入复杂度（条件分支单态化、phi 节点）—— 与"快速迭代收益"目标不匹配
3. 失败 fallback 到 CallIndirect 不破坏语义，只是错失优化
4. 未来若有 hot-path 数据证明 B / C 必要，可作独立 spec 增量

实现要点：
- `_localFuncAliases: Dictionary<string, string>` 跟踪当前作用域的 `local-var → fully-qualified-func-name`
- BindVarDecl 写入；任何后续对该 var 的赋值（`f = X;`）→ 立即 `_localFuncAliases.Remove(name)`
- BindIdent 查 dict，命中则填 ResolvedFuncName
- 进入嵌套 scope（block / lambda body / local fn）时按 scope 复制副本，离开 scope 时丢弃；不跨 scope 传播

### Decision 3: 是否单态化 BoundLambda 直接调用？

**问题**：`var sq = (int x) => x * x; sq(5);` 中 lambda 被 lift 成 `__lambda_0`。是否在 BindVarDecl 时也记录 alias？

**决定**：**是**。

实现：BindVarDecl 检测 init 是 `BoundLambda` 且 `Captures.Count == 0`（no-capture）→ 视为已 lift 函数，按 lifted 名字记入 `_localFuncAliases`。

> 带捕获的 lambda（`Captures.Count > 0`）**不**单态化 —— 调用还是要走 closure env 路径。详见 Decision 4。

### Decision 4: 带捕获的 closure 不单态化

**问题**：`int n = 5; var add = (int x) => x + n; add(3);` 是否单态化？

**决定**：**不单态化**。

理由：
- 调用 closure 必须传 env 作隐式第一参数（参见 closure.md §6 + L3-C-6）
- 单态化为 `Call(<lifted>, [3])` 会丢 env → 运行时崩溃
- 正确做法是 `MkClos + CallIndirect`（CallIndirect 知道要 prepend env）
- 未来若做"单态化 + env 内联展开"需要 closure-specific 单态化路径，属于 inline 优化范畴 → 独立 spec

### Decision 5: 顶层函数引用直接调用 — 已有行为保留

**问题**：`Helper();` 直接调用，BoundIdent 这里要不要也填 ResolvedFuncName？

**决定**：**填**。

实现：BindIdent 检测顶层函数名 / 静态方法名时也填 ResolvedFuncName（统一信号）。Codegen 优先级最高，避免重复决策。

> 已有行为（直接 Call top-level）实际上是 EmitBoundCall 的一个分支 —— 改造后该分支统一收敛到 ResolvedFuncName != null，逻辑更清晰。

### Decision 6: ResolvedFuncName 的命名空间形式

**问题**：填 fully-qualified name 还是 simple name？

**决定**：**fully-qualified**（与 IR Call.FuncName 一致）。

理由：CallInstr 用 FQ name，单态化结果直接复用 → 不需要二次 qualify。

## Implementation Notes

### TypeChecker 侧

`BoundIdent` 增加 `string? ResolvedFuncName`（默认 null），通过 record `with` expression 扩展。

`TypeEnv` 增加 per-scope dict `_localFuncAliases`：

```csharp
private readonly Stack<Dictionary<string, string>> _aliasScopeStack = new();

public void EnterScope() { _aliasScopeStack.Push(new(_aliasScopeStack.Peek())); }
public void ExitScope()  { _aliasScopeStack.Pop(); }
public void BindAlias(string localName, string fqName) => _aliasScopeStack.Peek()[localName] = fqName;
public void RemoveAlias(string localName) => _aliasScopeStack.Peek().Remove(localName);
public string? LookupAlias(string localName) =>
    _aliasScopeStack.Peek().TryGetValue(localName, out var fq) ? fq : null;
```

`BindVarDecl`（在 TypeChecker.Stmts.cs）：

```csharp
// 现有 binding 后：
if (initBound is BoundFuncRef fr) {
    env.BindAlias(varDecl.Name, fr.FullName);
} else if (initBound is BoundLambda lam && lam.Captures.Count == 0) {
    env.BindAlias(varDecl.Name, lam.LiftedName);
} else if (initBound is BoundIdent id && id.ResolvedFuncName is { } rfn) {
    env.BindAlias(varDecl.Name, rfn); // alias chain
}
```

`BindAssign`（任何对 local var 的赋值都清 alias）：

```csharp
case BoundIdentAssign ia:
    env.RemoveAlias(ia.Target);
    // ... 原有 binding ...
```

`BindIdent`：

```csharp
var resolved = env.LookupAlias(name)
    ?? TryResolveTopLevelFunc(name)
    ?? TryResolveStaticMethod(name);
return new BoundIdent(name, type, span, captureKind, ResolvedFuncName: resolved, ...);
```

### Codegen 侧

`EmitBoundCall`（FunctionEmitterCalls.cs）调整 callee resolution：

```csharp
private TypedReg EmitBoundCall(BoundCall call) {
    // 优先单态化路径
    if (call.Callee is BoundIdent { ResolvedFuncName: { } fqName }) {
        var argRegs = call.Args.Select(EmitExpr).ToList();
        var dst = Alloc(IrType.Unknown);
        Emit(new CallInstr(dst, fqName, argRegs));
        return dst;
    }
    // 立即调用 lambda
    if (call.Callee is BoundLambda { Captures.Count: 0, LiftedName: { } lifted }) {
        var argRegs = call.Args.Select(EmitExpr).ToList();
        var dst = Alloc(IrType.Unknown);
        Emit(new CallInstr(dst, lifted, argRegs));
        return dst;
    }
    // ...原有 CallIndirect / static / vcall 分支保留 ...
}
```

## Testing Strategy

### 单元测试（C#）

`src/compiler/z42.Tests/ClosureMonoCodegenTests.cs`（NEW）：

1. `LocalAlias_Resolves_To_Direct_Call` —— `var f = Helper; f();` IR 中无 `CallIndirectInstr`
2. `Reassigned_Var_Falls_Back_To_Indirect` —— 重赋值后退化
3. `NoCapture_Lambda_Direct_Call` —— `var sq = (int x) => x * x; sq(5);`
4. `Capturing_Lambda_Stays_Indirect` —— 验证带捕获不被错误单态化
5. `Function_Param_Stays_Indirect` —— `int Apply((int) -> int f, int x) => f(x);`

### Golden test

`src/runtime/tests/golden/run/closure_l3_mono/source.z42`（NEW）：

```z42
namespace Demo;
using Std.IO;

int Helper() { return 42; }

void Main() {
    var f = Helper;
    Console.WriteLine(f());

    var sq = (int x) => x * x;
    Console.WriteLine(sq(5));

    int n = 10;
    var add = (int x) => x + n;
    Console.WriteLine(add(3));
}
```

期望输出：
```
42
25
13
```

> 端到端验证语义不变；IR 层是否单态化由单元测试 dump IR 验证。

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # +5 ClosureMonoCodegenTests
./scripts/regen-golden-tests.sh
./scripts/test-vm.sh                                   # +1 closure_l3_mono
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| alias 跟踪误判把"非函数 var"识为 func ref | BindAlias 仅在 init 是 BoundFuncRef / no-capture BoundLambda / 已 alias 的 BoundIdent 时调用，类型严格 |
| scope 嵌套时 alias 泄漏 | 每个 block / lambda body / local fn 进入时 `EnterScope`，离开时 `ExitScope`；副本拷贝父 scope alias |
| 顶层函数与 local var 同名（shadowing）| LookupAlias 先查局部，再查顶层；shadow 行为正确 |
| 单态化命中率不可观测 | Open Question 中的 instrumentation 可作 follow-up，不阻塞 |
| 重构 BoundIdent 影响其他消费者 | record `with` 扩展兼容，所有现有 ctor 调用不变；新字段默认 null 不破坏既有行为 |
