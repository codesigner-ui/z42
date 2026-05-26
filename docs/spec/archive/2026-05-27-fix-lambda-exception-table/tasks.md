# Tasks: fix lambda / local-function exception table dropped during lift

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** Lambda 和 local function 的 IR emit 在 `FunctionEmitterExprs.Lambdas.cs` 里 4 处 `return new IrFunction(..., null, ...)` 硬编码 `ExceptionTable = null`，导致函数体内的 `try/catch` 收集到的 `_exceptionTable` 完全丢失。IR verifier 看到 catch 块的 `CatchReg` 没在 `defined` 集合（exception table 入口处定义的） → 报 `register rN used before definition in CopyInstr`。

**原因：** 实施 HttpClient timeout smoke test 时撞到该 bug，被迫把 catch body 抽 top-level fn 凑数。后续脚本移植里 lambda + try/catch 是高频组合（线程 worker 内部 swallow exception、retry 回调等），不修就反复绕。

**类型：** 最小化（compiler fix；纯局部，不破坏现有语义）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.Lambdas.cs` | MODIFY | 4 处 `IrFunction(..., null, ...)` 改为 `..., _exceptionTable.Count > 0 ? _exceptionTable : null, ...`（已是其他 emit*Function 方法的标准 pattern） |
| `src/tests/exceptions/lambda_try_catch/` | NEW | golden test 覆盖：lambda body 内 try/catch / local function 内 try/catch |
| `src/compiler/z42.Tests/IrVerifierTests.cs` | MODIFY 或 NEW | C# unit test 直接断言 IR-verify 通过（更快锁定回归） |

**只读引用：**
- `src/compiler/z42.IR/IrVerifier.cs:86-90` —— exception table 把 CatchReg 加进 `defined` 的逻辑
- `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs:198,293` —— 正确 pattern 参考

## Tasks

- [x] 1.1 修 `EmitLifted(BoundLambda)` ExceptionTable null → `_exceptionTable.Count > 0 ? _exceptionTable : null`
- [x] 1.2 修 `EmitLiftedLambdaWithEnv` 同上
- [x] 1.3 修 `EmitLiftedLocalFunction` 同上
- [x] 1.4 修 `EmitLiftedLocalFunctionWithEnv` 同上
- [x] 1.5 写 golden test：lambda body 含 try/catch，验证编译通过 + 运行行为正确
- [x] 1.6 验证：`dotnet build` 干净，`regen-golden-tests.sh` 包括新 case 全过
- [x] 1.7 归档 + commit + push

## 备注

- 这是 `compiler-future-lambda-try-catch-ir-verify-bug` backlog 项（add-httpclient-timeout 实施期记下），本 spec 直接关掉。
- 修复后 `add-httpclient-timeout` 的 smoke test 可以从"用 top-level fn 凑数"回到"inline lambda body + try/catch"形式；但既有 smoke 既然已经过了就不主动重写。
- 4 处 fix 完全同形（一行替换），单元测试覆盖 lambda + local fn 两条路径足够；不必逐条 path 独立 fixture。
