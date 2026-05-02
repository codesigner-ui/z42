# Proposal: 闭包单态化（编译期解析已知 callee → 直接 Call）

## Why

L3 闭包当前一律走 `CallIndirect` 间接调度（Tier C 堆擦除路径），即使 callee 在编译期是静态可知的：

- `let f = MyFunc; f();` — `f` 实际上就是 `MyFunc` 的别名，但生成 `CallIndirect`
- `var sq = (int x) => x * x; sq(5);` — `sq` 是 no-capture lambda，编译器已合成具名 `__lambda_0`，调用时仍然 `CallIndirect`
- `list.Filter(x => x > 0)` 中 `Filter` 形参类型若是 `(T) -> bool`，每次调用都走间接 dispatch

间接 dispatch 的 overhead：
- runtime VM 字符串查 `fn_entries` HashMap 命中
- JIT 中要 lookup 函数指针并通过 helper 调用

**单态化收益**：编译期已知的 callee 直接 emit `Call`，跳过运行时查找。命中后 hot path 与"普通函数调用"性能完全一致。

## What Changes

- **TypeChecker 层**：扩展 `BoundIdent` 携带 `ResolvedFuncName: string?` —— 当某个标识符可在编译期解析为顶层函数 / 静态方法 / 已知 closure / 已知 lambda 名字时填入；否则为 null
- **TypeChecker 层（流分析）**：在 BindBlock / BindStmts 中，把"local 变量初始化为 BoundFuncRef / BoundLambda"的简单赋值流追踪进 BoundIdent.ResolvedFuncName。仅做线性单赋值情形，不做完整 SSA / dataflow（视下一个反馈再扩）
- **Codegen 层**：改造 `EmitCallIndirect` / `EmitBoundCall` 中的 callee 解析路径：
  - 若 BoundIdent.ResolvedFuncName 非 null → 发 `Call(<resolved-name>, args)` 替代 `CallIndirect`
  - 若 callee 是 `BoundLambda` 字面量 → 直接 emit `Call(<lifted-lambda-name>, args)`
- **保留 fallback**：任何无法解析的情形（参数 / 字段 / 跨函数返回的 closure）继续走 `CallIndirect`，运行时语义不变

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | `BoundIdent` 新增 `string? ResolvedFuncName` 字段（默认 null）|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | `BindIdent` 在解析 ident 后查"该名字是否已知是函数引用"，填 ResolvedFuncName |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs` | MODIFY | `BindVarDecl` / 局部赋值时记录"local var → resolved func name"映射，供后续 BindIdent 命中 |
| `src/compiler/z42.Semantics/TypeCheck/TypeEnv.cs` | MODIFY | 新增 `_localFuncAliases: Dictionary<string, string>` per scope，跟踪 `let f = SomeFunc;` 的别名链 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` | MODIFY | `EmitBoundCall.Free` 优先用 ResolvedFuncName → 发 `Call`；fallback 现有 `CallIndirect` |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | `EmitLambdaLiteral` 调用场景：被立即调用的 lambda 直接 `Call(<lifted>)` |
| `docs/design/closure.md` | MODIFY | 新增 §"单态化"章节描述启发式 + 兜底语义 |
| `src/runtime/tests/golden/run/closure_l3_mono/source.z42` | NEW | 端到端验证（dump IR 检查 `Call` vs `CallIndirect`）|
| `src/runtime/tests/golden/run/closure_l3_mono/expected_output.txt` | NEW | golden 期望输出 |
| `src/runtime/tests/golden/run/closure_l3_mono/source.zbc` | NEW | regen 产物 |
| `src/compiler/z42.Tests/ClosureMonoCodegenTests.cs` | NEW | 单元测试：dump IR 验证特定模式被单态化 |

**只读引用**：

- `src/runtime/src/jit/helpers_closure.rs` — JIT CallIndirect 行为（不改，验证 fallback 仍工作）
- `src/runtime/src/interp/exec_instr.rs` — interp CallIndirect 行为
- `src/compiler/z42.IR/IrModule.cs` — `CallInstr` / `CallIndirectInstr` shape
- `spec/archive/2026-05-02-impl-closure-l3-core/design.md` — Tier C 堆擦除决策背景

## Out of Scope

- ❌ 不引入完整 SSA / dataflow 分析；只跟踪"局部 var 初始赋值"的简单单赋值情形
- ❌ 不做"closure → 内联展开"（inlining 是独立优化，等基准数据触发）
- ❌ 不改 IR 二进制格式（zbc 不变）
- ❌ 不改 VM 行为（CallIndirect 路径保留为 fallback）
- ❌ 不做跨函数 / 跨包的 callee 解析（只在单函数 scope 内分析）

## Open Questions

- [ ] `let f = SomeFunc; if (cond) { f = OtherFunc; } f();` 这种条件重赋值的情形 → 简单单赋值检测会失败，回退 CallIndirect。能接受吗？建议 **能接受**（保守 fallback，非破坏行为）
- [ ] 单态化命中率的验证方式：是否在 IR 层加 statistics counter，跑 stdlib + golden tests 后报告"N% calls 走 Call、(100-N)% 走 CallIndirect"？建议**作为可选 instrumentation**，不阻塞主线
