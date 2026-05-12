# Tasks: Split BindCall — Function-Level Refactor (D-12)

> 状态：🟢 已完成 | 创建：2026-05-12 | 完成：2026-05-12
> 类型：refactor（单文件 — 最小化模式）

## 实施备注

- 实际拆出 14 个私有方法（比初稿计划的 9 个多 5 个）：泛型参数 receiver 路径拆为 `BindMemberCallOnGenericParam` + `BindGenericParamBaseClassMethod` + `TryBindGenericParamInterfaceMethod`；free ident 路径拆为 `BindFreeIdentCall` + `BindFreeTopLevelCall` + `BindFreeFuncValuedVarCall`。
- 所有方法 ≤ 57 行（60 硬限内）；最大的 BindMemberCallOnClass = 57 行（仍接近软限 40，但 visibility 检查 + import fallback + reorder 三段紧耦合，进一步拆会引入更多 helper 参数表噪音）。
- 零行为变更：1233/1233 C# 测试 + 320/320 VM golden + cross-zpkg + stdlib dogfood 全部不动。
> 关闭：D-12 introduce BindCall 函数级拆分（split-typechecker-calls 残留，
> [`compiler/compiler-architecture.md`](../../../design/compiler/compiler-architecture.md) Deferred 段 + roadmap Deferred Backlog Index）

## 变更说明

`TypeChecker.BindCall`（[TypeChecker.Calls.cs:14-456](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs)）单方法 ~440 行，远超 60 行硬限。按 receiver / callee shape 拆为 8 个职责单一的私有方法，零行为变更。

## 原因

`.claude/rules/code-organization.md` 硬性约束：函数 60 行硬限超出必须拆分。
之前 add-named-arguments / extend-named-args-shim 接入时只能在巨函数内继续堆代码，使每次扩展心智成本指数级增长。本次拆完后未来命名参数 / 重载 / 跨 CU 路径扩展可独立改一个分支函数。

## 拆分方案

| 方法 | 行号 | 职责 |
|-----|-----|-----|
| `BindCall`（entry） | ~14- | dispatcher，仅做 Z1001 + 三分支 if-chain |
| `BindStaticClassCall` | 22-65 | `ClassName.Method(args)` 静态方法 |
| `BindMemberCallOnUnknownTarget` | 84-111 | `Console.WriteLine` / 原始类型 / stdlib 类静态调用 |
| `BindMemberCallOnInstantiated` | 117-156 | `recv.M(args)` where `recv: Z42InstantiatedType` |
| `BindMemberCallOnClass` | 158-212 | `recv.M(args)` where `recv: Z42ClassType` |
| `BindMemberCallOnInterface` | 214-235 | `recv.M(args)` where `recv: Z42InterfaceType` |
| `BindMemberCallOnGenericParam` | 237-294 | `recv.M(args)` where `recv: Z42GenericParamType`（base + iface + static abstract） |
| `BindMemberCallOnPrimitive` | 296-344 | 原始类型 receiver + L3 primitive-as-struct |
| `BindFreeFunctionCall` | 346-456 | bare name / 顶层函数 / 函数值变量 / 非 ident callee |

返回值统一为 `BoundExpr`；分支函数在不匹配时返回 `null` 让 entry 继续 fall-through 到下一分支。member call 分支由 `BindMemberCall` 统一调度（按 `recvExpr.Type` 类型 dispatch）。

## 进度概览

- [x] 阶段 1: 拆分 BindCall → 8 个私有方法
- [x] 阶段 2: 验证 + GREEN（test-all.sh 5 stage）
- [x] 阶段 3: 关闭 D-12 — 同步 roadmap + compiler-architecture.md
- [x] 阶段 4: 归档

## 阶段 1: 拆分

- [x] 1.1 [TypeChecker.Calls.cs](../../../../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Calls.cs) — 提取 `BindStaticClassCall(string clsName, string staticMember, CallExpr, TypeEnv)`
- [x] 1.2 同上 — 提取 `BindMemberCall(MemberExpr, CallExpr, TypeEnv)` 调度器，把 `recvExpr.Type` switch 拍平
- [x] 1.3 同上 — 提取 `BindMemberCallOnUnknownTarget(IdentExpr target, MemberExpr, CallExpr, TypeEnv)`
- [x] 1.4 同上 — 提取 `BindMemberCallOnInstantiated(BoundExpr recv, Z42InstantiatedType inst, MemberExpr, CallExpr, TypeEnv)`
- [x] 1.5 同上 — 提取 `BindMemberCallOnClass(BoundExpr recv, Z42ClassType ct, MemberExpr, CallExpr, TypeEnv)`
- [x] 1.6 同上 — 提取 `BindMemberCallOnInterface(BoundExpr recv, Z42InterfaceType iface, MemberExpr, CallExpr, TypeEnv)`
- [x] 1.7 同上 — 提取 `BindMemberCallOnGenericParam(BoundExpr recv, Z42GenericParamType gp, MemberExpr, CallExpr, TypeEnv)`
- [x] 1.8 同上 — 提取 `BindMemberCallOnPrimitive(BoundExpr recv, MemberExpr, CallExpr, TypeEnv)`
- [x] 1.9 同上 — 提取 `BindFreeFunctionCall(CallExpr, TypeEnv)`

## 阶段 2: 验证

- [x] 2.1 dotnet build src/compiler/z42.slnx — 无 error / warning
- [x] 2.2 dotnet test — 1233/1233 passed（零回归 = 拆分等价证明）
- [x] 2.3 ./scripts/test-all.sh — 5 stage 全绿

## 阶段 3: 关闭 D-12

- [x] 3.1 [compiler-architecture.md Deferred 段](../../../design/compiler/compiler-architecture.md) — 移除 D-12 条目
- [x] 3.2 [roadmap.md Deferred Backlog Index](../../../roadmap.md) — 移除 D-12 索引行

## 阶段 4: 归档

- [x] 4.1 mv `docs/spec/changes/split-bind-call/` → `docs/spec/archive/2026-05-12-split-bind-call/`
- [x] 4.2 commit + push
