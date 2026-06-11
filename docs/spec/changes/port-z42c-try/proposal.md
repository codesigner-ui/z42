# Proposal: port-z42c-try — 异常 try/catch/throw 整链

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（char 归档后接力）

## Why

interface·异常·闭包三大件按"对编译 z42c 自身的贡献"排序，异常居首：z42c 自身源码（如未来的 driver IO 错误路径）与 stdlib 大量使用 try/catch/throw；syntax 早已解析（6a：TryStmt/CatchClause/ThrowStmt），typecheck/codegen/写入器三段全缺。zbc FUNC 的 excCount 字段恒写 0——ExceptionTable 编码是现成接入点。

## What Changes

- **TR-1 semantics typecheck**：`BoundTry/BoundCatchClause/BoundThrow`；绑定 try 体/catch 子句（ExType 解析→FQ 异常类名，无类型 catch→null；catch 变量入 scope，类型=解析类或 Ref）/finally 体/throw 表达式
- **TR-2 codegen**：镜像 C# EmitBoundTryCatch——标签 try_start/try_end/after_try/finally；表条目 (tryStart, tryEnd, catch_start, ExceptionTypeName, catchReg)；无 catch 有 finally → 合成 catch-all "*"+rethrow（ThrowTerm 复用）；throw 语句 → 求值+ThrowTerm
- **TR-3 ir/writer**：IrFunction + ExceptionTable（**可变公字段**，免 16 参 ctor 全站改）；ZbcWriter FUNC 段 exc 条目编码（blockIdx u16×3 + catchType pool idx|0xFFFFFFFF + catchReg u16，**条目在 blockOffsets 后 instrBytes 前**，C# 实证）；InternPoolStrings 补 exc 串（TryStart/TryEnd/CatchLabel/CatchType——位置在块串后、LineTable file 前，C# 实证行 280-283）
- **TR-4 验证**：trycheck zbc 第 5 源（throw new + 分类 catch + catch-all + finally + oracle）→ 执行 + **byte-compare 5/5**；单测（typecheck/codegen ≥2）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.semantics/src/Bound.z42` | MODIFY | BoundTry/BoundCatchClause/BoundThrow |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | 绑定三式 |
| `src/z42c/z42c.semantics/src/FunctionEmitter.z42` | MODIFY | EmitTry/throw lowering |
| `src/z42c/z42c.semantics/src/EmitContext.z42` | MODIFY | ExceptionTable 累积 |
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY | 单测 |
| `src/z42c/z42c.ir/src/IrModule.z42` | MODIFY | IrExcEntry + IrFunction 可变表字段 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY | exc 编码 + intern 位 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | trycheck 第 5 源 |
| `docs/design/compiler/self-hosting.md` | MODIFY | 同步 |

**只读引用**：C# `FunctionEmitterStmts.Branches.cs`（EmitBoundTryCatch/EmitTryCatchClauses）/`ZbcWriter.cs`（exc 编码 660-690 + intern 280-283）/`IrModule.cs`（IrExceptionEntry）。

## Out of Scope
- catch when 过滤、异常类型层级检查（catch 顺序窄化警告）、自定义异常类定义的 typecheck 完备性（corpus 用 stdlib Exception）

## Open Questions
- [ ] Q1：catch 变量寄存器（C# Alloc(Ref) 在 entry 前？分配时机影响寄存器序）——字节校准制
