# Design: Named Arguments

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Parse (ExprParser.Atoms.ParseCallArgWithOptionalModifier)      │
│                                                                 │
│   `f(a, name: 1, ref x, out var y)`                             │
│      ↓ lookahead IDENT ':' at arg start → named                 │
│   List<Argument> [                                              │
│     { Name=null, Value=IdentExpr("a"), Mod=None },              │
│     { Name="name", Value=IntExpr(1),   Mod=None },              │
│     { Name=null, Value=IdentExpr("x"), Mod=Ref  },              │
│     { Name=null, Value=IdentExpr("y"), Mod=Out, OutVar="y" },   │
│   ]                                                             │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  TypeCheck.BindArgsToParams (NEW algorithm in TypeChecker.Calls)│
│                                                                 │
│   Phase A — Overload filter:                                    │
│     ∀ candidate: required(c) ≤ argCount ≤ total(c)              │
│                  ∀ named names must be in c.params              │
│   Phase B — For best candidate, bind:                           │
│     1. positional[i] → param[i]                                 │
│     2. named[k] → param[paramIdxByName[k.Name]]                 │
│        diagnose Z0501..Z0505                                    │
│     3. missing params → FillDefaults at IrGen                   │
│                                                                 │
│   Output: BoundCall.Args = List<BoundExpr> in param order       │
│           BoundCall.OriginalNamedIndices = HashSet<int>?         │
│             (which positions originated from named form,         │
│              for dump / error rendering only)                    │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  IrGen (FunctionEmitterCalls.cs — UNCHANGED)                    │
│                                                                 │
│   BoundCall.Args already positional → emit CallInstr /          │
│   ObjNewInstr / VCallInstr as today. FillDefaults / EmitType-   │
│   Default cover any remaining trailing or middle-hole gaps.     │
└─────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: `CallExpr.Args` 类型直接换为 `List<Argument>`（侵入式）

**问题**：保留 `List<Expr>` 加平行 `List<string?> Names` 字段 vs 升级为 `List<Argument>`。

**决定**：升级为 `List<Argument>`。
- 单一真相来源：每个实参的所有元信息（Name / Value / Modifier / Span）聚到一个 record
- 现有 `ref/out/in` 已通过包装类型（ArgModifierExpr / OutVarDecl）侧路传递，本就需要规整
- ~30 个 internal 调用点（event/delegate synth、tests）是 mechanical 改造，不增加架构复杂度

`Argument` 定义：
```csharp
public sealed record Argument(
    string?       Name,      // null 表示位置实参
    Expr          Value,
    ArgModifier   Modifier,
    OutVarDecl?   OutVar,    // 仅 Modifier=Out 且有 inline `var` 时非 null
    Span          Span);
```

OutVarDecl 从 `Value` 旁侧位升级为 Argument 直接字段，让 `f(target: out var x)` 形式与位置形态对称。

### Decision 2: 错误码 Z0501–Z0505（新增 5 个；catalog 当前 Z0xxx 序列尾部有空位）

| 错误码 | 含义 | Span 锚点 |
|-------|------|----------|
| `Z0501 PositionalAfterNamed` | named arg 之后出现位置 arg | 该位置 arg 的 Span |
| `Z0502 UnknownArgumentName` | named arg 的 name 在 callee 参数表里不存在（or 跨 overload 无候选剩余）| name token 的 Span |
| `Z0503 DuplicateArgumentName` | 同一 call 重复 named arg | 第二次出现的 name token Span |
| `Z0504 ParameterDoublySpecified` | param 既被位置 arg 又被 named 指定 | named arg 的 Span |
| `Z0505 MissingRequiredArgument` | 绑定后某 required param 仍未填 | call 整体 Span |

具体编号在 implementation 时按 `DiagnosticCatalog.cs` 已用区间选 next-available（这 5 个保留为占位）。

### Decision 3: Overload 解析采用两阶段算法

**Phase A — Candidate Filter**：对每个 overload candidate `c`：
1. 假设 args 数为 `n`，candidate 参数数为 `p`，必填参数（无 default）数为 `r`
2. **arity 范围**：`r ≤ n ≤ p`（否则参数数对不上）
3. **named 名称包含**：所有 named arg 的 name 必须出现在 `c.Params` 中（否则该 overload 直接不可能）

**Phase B — Best-Match Score**：对剩余 candidates 复用现有 type-conversion 评分逻辑（在 reorder 后的位置参数上）。无单一最优 → `OverloadAmbiguous`（复用现有错误码）。

实现位置：[TypeChecker.Calls.cs](../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs) 当前的 overload resolution 入口拓展 candidate 过滤步骤。

### Decision 4: `out var x` 与 named 形式正交（保留所有现有规则）

**决定**：`f(paramName: out var x)` 合法。
- Argument record 增 `OutVarDecl?` 字段（仅 Modifier=Out 且 inline 时非 null）
- OutVarDecl scope 规则不变：覆盖到所在语句结束（spec add-out-var）
- 与名字解析正交：name 用于绑定参数位置，OutVarDecl 用于声明 outer-scope 局部

### Decision 5: BoundCall 添加可选 `OriginalNamedIndices: HashSet<int>?`

仅为诊断 / `--dump-bound` / `--dump-ast` 渲染使用。IrGen 不读此字段；`null` 当且仅当 call 全为位置形态（节省 alloc）。

**为什么不在 Argument 里加 `OriginalIndex` 字段一路传到 IR**：BoundCall 之后是 IR 层，强假设位置形态，混入命名残留会让 reader / runtime 误以为 IR 有 name 概念。保留在 Bound 层最干净。

## Binding Algorithm（伪码）

```csharp
BoundCall BindCall(CallExpr call, FunctionSymbol callee)
{
    var args = call.Args;
    var params = callee.Params;

    // Phase 1: positional-before-named guard
    bool sawNamed = false;
    foreach (var a in args)
    {
        if (a.Name != null)       sawNamed = true;
        else if (sawNamed)        diag(Z0501, a.Span);
    }

    // Phase 2: name lookup map (param.Name → index)
    var paramByName = params
        .Select((p, i) => (p.Name, i))
        .ToDictionary(t => t.Name, t => t.i);

    // Phase 3: build resolution by index
    var resolved = new BoundExpr?[params.Count];
    var namedIndices = new HashSet<int>();
    var seenNames = new HashSet<string>();

    // 3a: positional
    for (int i = 0; i < args.Count && args[i].Name is null; i++)
        resolved[i] = TypeCheckArg(args[i], params[i]);

    // 3b: named
    for (int i = ...; i < args.Count; i++)  // continues from end of positional
    {
        var a = args[i];
        if (!seenNames.Add(a.Name))                { diag(Z0503, a.NameSpan); continue; }
        if (!paramByName.TryGetValue(a.Name, out int p))
                                                   { diag(Z0502, a.NameSpan); continue; }
        if (resolved[p] != null)                   { diag(Z0504, a.Span);    continue; }
        resolved[p] = TypeCheckArg(a, params[p]);
        namedIndices.Add(p);
    }

    // Phase 4: defaults / missing
    for (int i = 0; i < params.Count; i++)
        if (resolved[i] is null && params[i].Default is null)
            diag(Z0505, call.Span);
    // missing-with-default → leave null, IrGen.FillDefaults handles it

    return new BoundCall(
        ...,
        Args: resolved.Where(x => x != null).ToList(),   // packed positional (see Note below)
        OriginalNamedIndices: namedIndices.Count > 0 ? namedIndices : null);
}
```

> **Note**：上述"packed positional 留空位让 IrGen 补"对接 FillDefaults — 当前 FillDefaults 按 `argRegs.Count < required` 判定，需要小幅升级支持"中间空位"标记。详见 Implementation Notes。

## Implementation Notes

### `FillDefaults` 升级

现状（[FunctionEmitterCalls.cs:229](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs#L229)）：
```csharp
private List<TypedReg> FillDefaults(string qualifiedName, List<TypedReg> argRegs)
{
    if (argRegs.Count >= parms.Count) return argRegs;
    ... // 只补尾部
}
```

升级形态：
- BoundCall.Args 携带显式 "空位标记"（约定：`List<BoundExpr?>` 长度等于 params.Count，`null` 表示空位）
- FunctionEmitterCalls.cs 改成迭代每个 param 位置；非空位 → emit 表达式；空位 → emit default
- BoundCall.Args 类型从 `List<BoundExpr>` 升级为 `List<BoundExpr?>`（影响 BoundExprVisitor，但 visitor 已是 mechanical 适配）

实施时机：与 binding algorithm 在同一 PR 内（无 dirty 中间态）。

### Parser 消歧细节

- `ParseCallArgWithOptionalModifier` 入口加 lookahead：`Current.Kind == IDENT && Peek(1).Kind == Colon`
- 排除 `IDENT::ident`（如有 `::` token）—— implementation 时核对 z42 lexer 现有 token 集
- 命中：consume name + `:`，然后递归调用同一 arg parser 解析 inner expression（保留所有 modifier 支持）
- 不命中：fall through 现有路径

### TypeCheck 对 imported（跨 CU）callee 的处理

`Z42FuncType.Params` 已含 Name（[Z42Type.cs](../../../src/compiler/z42.Semantics/TypeCheck/Z42Type.cs)），imported 方法可同样按名字绑定。无需 wire format 改动（TSIG / SIGS 已携带 param names）。

### 现有 `IDENT:` token 序列的潜在冲突

- 标号语法（`label:` 语句）：仅在 statement 级别，不在 expression 级别。call 内部不冲突。
- 三目 `?:`：`a ? b : c` 中 `:` 在 inner expression 内部，不会被 arg-start lookahead 触发（因为 arg-start 时还没见 `?`）。
- 类型注解 `var x: int = ...`：仅在 declaration 级别。

无冲突。

## Testing Strategy

### 单元测试 `NamedArgumentsTests.cs`（C#）

**Parser 层**：
- `Parse_SimpleNamedArg_ProducesArgumentWithName`
- `Parse_NamedArgWithRef_ProducesArgWithModifier`
- `Parse_NamedArgWithOutVar_ProducesArgWithOutVarDecl`
- `Parse_TernaryInPositionalArg_NotConfusedWithNamed`

**TypeCheck 层**：
- `Bind_OutOfOrderNamed_ReordersByParamPosition`
- `Bind_MiddleDefault_SkippedWithNamed`
- `Bind_UnknownName_EmitsZ0502`
- `Bind_DuplicateName_EmitsZ0503`
- `Bind_PositionalAfterNamed_EmitsZ0501`
- `Bind_ParamDoublySpecified_EmitsZ0504`
- `Bind_MissingRequired_EmitsZ0505`
- `Overload_NameSetDisambiguates_ChoosesCorrectCandidate`
- `Overload_NoNameMatch_EmitsZ0502`

**Codegen 层**：
- `Codegen_NamedArgs_EmitsCallInstrInParamOrder`
- `Codegen_CtorNamedArgs_EmitsObjNewInstrInParamOrder`

### Golden E2E `src/tests/calls/named_args/`

一个综合 z42 源：函数+方法+ctor 各一次 named call，包含中间默认 + ref/out 组合。expected_output 验证执行结果。

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

## Deferred / Future Work

- **Lambda / delegate indirect call 的 named args**：z42 与 C# 保持一致，indirect call 不支持（lambda 调用点拿不到静态参数名）。如未来加 `Func<T, U>` 的 design-time 类型信息保留 param name，可独立 spec 启用
- **Reflection 读 param name**：stdlib API 范畴（`Std.Reflection.GetParameters`），独立 spec
- **`params` array 与 named 的交互**：z42 `params` 形态稳定后单独 spec
