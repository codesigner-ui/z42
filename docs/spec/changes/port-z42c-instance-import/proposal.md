# Proposal: port-z42c-instance-import — 实例方法跨包命中链（import 消费侧下半场）

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（import 归档后接力）

## Why

import MVP 只覆盖静态类方法；实例调用仍是无差别 VCall——两个缺口：① imported 类对象的实例调用（`sb.Append`）不追踪依赖 ns → DEPS 缺条目（C# 注释明示该 bug 曾导致运行期 "VCall: function not found"）；② prim receiver（`s.Substring`）在 z42c typecheck 直接报错 "call on non-class"，stdlib 字符串方法完全不可用。补齐后 corpus 可扩到真实字符串/StringBuilder 程序。

## What Changes（镜像 C# EmitInstanceBoundCall 全路径）

- **IC-1 receiver-aware 守卫**（ExprEmitter）：receiver 类链（含 imported）自有该方法 → **VCall**（vtable 赢，防 DepIndex 劫持——fix-instance-method-binding-receiver-aware 镜像）；VCall fallback 且 receiver 是 imported 类 → **TrackDepNamespace(receiver ns)**（C# 行 198-200 镜像；需 ctx.ImportedClassNamespaces）
- **IC-2 DepIndex instance 捷径**：守卫未命中（prim/unknown receiver）→ `Deps.GetInstance(method, argc)` → `CallInstr(QualifiedName, [recv]+args)` + Track
- **IC-3 typecheck prim receiver 放行**：非类 receiver 的方法调用 → 吸收为 instance BoundCall（OwnerClass=prim 名，ret Unknown），不再报错（镜像 C# 松绑定；DepIndex 在 codegen 做真实解析）
- **IC-4 验证**：e2e 第 4 工程 textapp（`using Std; using Std.Text;` StringBuilder.Append×2 + ToString + string.Substring + Console.WriteLine）→ 直跑输出断言 + **全文件 byte-compare**；单测 ≥2（typecheck prim 吸收 / codegen instance 命中 dump）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | IC-1/2 instance 分支重构 |
| `src/z42c/z42c.semantics/src/EmitContext.z42` | MODIFY | ImportedClassNamespaces + 链上有法查询 |
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY | imported ns 表透传 |
| `src/z42c/z42c.semantics/src/IrDump.z42` | MODIFY | BuildModuleD 穿 imported ns 表 |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | IC-3 prim receiver 吸收 |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | prim 吸收单测 |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY | （如 dump 形态可断言）instance 命中单测 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | e2e 第 4 工程 textapp |
| `docs/design/compiler/self-hosting.md` | MODIFY | 状态同步 |

**只读引用**：C# `FunctionEmitterCalls.cs`（EmitInstanceBoundCall/ReceiverChainHasMethod/EmitInstanceVCallFallback）。

## Out of Scope
- builtin-collection（Array.Length 等 BuiltinInstr 路径——数组成员 typecheck 未接）、FillDefaults/默认参、Console 变参糖、arity 重载解析（$N 选择）

## Open Questions
- [ ] Q1：prim receiver BoundCall 的 ret 类型（Unknown vs C# 实际）以同源字节校准——无需预先裁决
