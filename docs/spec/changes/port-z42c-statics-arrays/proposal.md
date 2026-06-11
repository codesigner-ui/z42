# Proposal: port-z42c-statics-arrays — 静态字段/常量 + 数组创建（自举首包临门 G3/G4）

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（package-symbols 归档后接力）

## Why

z42c.core 自编译只剩两缺口（盘点实证）：G3 静态字段访问（`DiagnosticSeverity.Error`——static class+int 常量 = z42 enum 惯用法）与 G4 数组创建（`new Diagnostic[8]`——typed array+count 模式根基）。落地即 **z42c 第一次编译自己的包**。探针字节真相已拿：`{文件stem}.__static_init__` 合成函数置函数表**首位**（每字段 init→StaticSet）；StaticGet 0x62/StaticSet 0x63（FQ "Sev.Warn" 串）；ArrayNew 0x80（7B：tag+dst+size reg+elemTag）。

## What Changes

- **SA-1 数组创建**：parser `new T[size]`（ArrayNewExpr）→ BoundArrayNew → ArrayNewInstr（编码+REGT+intern 无串）；arr.Length e2e 顺带解锁
- **SA-2 静态访问**：typecheck 裸类名成员读（非调用）→ 静态 FieldSymbol → BoundStaticGet → StaticGetInstr（Qualify(类)+"."+字段 串）
- **SA-3 __static_init__ 合成**：IrGen 收集全 CU 静态字段 init → 合成 `{stem}.__static_init__`（函数表首位、ret void、0 参；每字段 init 表达式→StaticSet）；IrGen 增 SourceStem（IrDump 穿文件名）
- **SA-4 验证**：sacheck 第 7 zbc 源（静态类常量 + new int[n] + 下标/Length + oracle，探针程序即 corpus）→ 执行 + **byte-compare 7/7**；**z42c.core 自编译冒烟达成**（7 文件 0 错产包——gate 新常驻步）；单测 ≥3

## Scope（允许改动的文件）

| 文件路径 | 变更类型 |
|---------|---------|
| `src/z42c/z42c.syntax/src/Ast.z42` | MODIFY（ArrayNewExpr）|
| `src/z42c/z42c.syntax/src/Parser.z42` | MODIFY（new T[n]）|
| `src/z42c/z42c.semantics/src/Bound.z42` | MODIFY（BoundArrayNew/BoundStaticGet）|
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY（两式绑定）|
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY（两式发射）|
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY（static_init 合成 + SourceStem）|
| `src/z42c/z42c.semantics/src/FunctionEmitter.z42` | MODIFY（如需 init 发射助手）|
| `src/z42c/z42c.semantics/src/IrDump.z42` | MODIFY（stem 穿线）|
| `src/z42c/z42c.ir/src/IrInstr.z42` | MODIFY（三指令）|
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` | MODIFY（ops）|
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcInstr.z42` | MODIFY（编码+intern）|
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY（REGT）|
| `src/z42c/z42c.semantics/tests/typecheck/typecheck_tests.z42` | MODIFY |
| `src/z42c/z42c.semantics/tests/codegen/codegen_tests.z42` | MODIFY |
| `scripts/xtask_compiler_z42.z42` | MODIFY（sacheck + core 冒烟步）|
| `docs/design/compiler/self-hosting.md` | MODIFY（自举首包里程碑）|

**只读引用**：C# ZbcWriter.Instructions.cs（编码 155-190）/IrGen static-init 段；/tmp/sa_cs.zbc 探针。

## Out of Scope
- 静态字段运行期赋值（`Sev.X = 5`——core 不用）、数组字面量 `new T[]{...}`（ArrayNewLit——core 不用？盘点定）、静态属性

## Open Questions
- [ ] Q1：build 路径 static_init 的 stem（文件 stem ✓ C# _cuStem 实证）与 DBUG/SIGS 细节 corpus 校准
