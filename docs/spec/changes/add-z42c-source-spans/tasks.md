# Tasks: add-z42c-source-spans — span 链（AST → Bound → LineTable → DBUG）

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-10 | 子系统锁：z42c（zbc-writer 归档后接力）
> **未经 User 批准不动代码（Spec-First gate）。**

## 进度概览
- [ ] SP-1 syntax：AST 42 节点携 Span（尾参）+ Parser 构造点填入 + 调用点机械更新
- [ ] SP-2 semantics：Bound 节点携 Span + binder 透传 + 诊断真实位置（删 _noSpan 用法）
- [ ] SP-3 codegen：TrackLine（同行去重/basename）+ LocalVar 声明序表 → IrFunction 两表
- [ ] SP-4 ir：IrLineEntry/LocalVarEntry + ZbcWriter DBUG section + HasDebug flag
- [ ] SP-5 验证：`int F(){return 5;}` 全文件 byte-identical golden + xtask e2e byte-compare 步（三源 z42c vs C# diff）+ gate 全绿

## SP-1：syntax（机械大头）
- [ ] 1.1 Ast.z42/Stmt.z42/Decl.z42/TypeExpr.z42：每 sealed class 加 `public Span Span;` 尾参
- [ ] 1.2 Parser.z42：每构造点取首 token 行/列 + file 填 Span
- [ ] 1.3 syntax 各测试直构节点补尾参；+ span 断言用例（≥2）
- [ ] 1.4 验证：`xtask test compiler-z42` syntax 单元全绿（Dump 断言零变化）

## SP-2：semantics 类型检查半
- [ ] 2.1 Bound.z42 各节点加 Span 尾参；TypeChecker 透传
- [ ] 2.2 诊断点改真实 span；删 `_noSpan()`
- [ ] 2.3 测试直构 Bound 节点补尾参；+ 诊断位置断言用例（≥1）

## SP-3：codegen
- [ ] 3.1 EmitContext：`LastLine` + `TrackLine(span)`（去重 + basename）+ LineTable 增长数组 + Locals 声明序并行表
- [ ] 3.2 FunctionEmitter._emitStmt 入口 TrackLine；收尾导出两表进 IrFunction
- [ ] 3.3 IrGen file 名穿线

## SP-4：ir
- [ ] 4.1 IrModule.z42：IrLineEntry/IrLocalVarEntry + IrFunction 两表字段
- [ ] 4.2 ZbcWriter：hasDebug 检测 + flags|=0x02 + DBUG 第 9 section（u16 lineCount + 条目 + u16 varCount + 条目）+ intern（file basename + 局部名，**intern 时机对齐 C# InternPoolStrings 的 LineTable/LocalVar 段**）

## SP-5：验证
- [ ] 5.1 zbc_tests：`int F(){return 5;}` 全文件 hex 对 C# 同源（`dotnet z42c.dll <src> --emit zbc` 截取）
- [ ] 5.2 xtask e2e：byte-compare 步（selfcheck/callcheck/typecheck 三源 z42c vs C# 逐字节 diff）
- [ ] 5.3 `xtask test compiler-z42` 全绿；README（syntax/semantics/ir）+ self-hosting.md 状态同步

## 备注
- C# 权威：FunctionEmitter.Helpers.TrackLine / BaseName；ZbcWriter.BuildDbugSection / InternPoolStrings（LineTable.File + LocalVar.Name 的 intern 位置）。
- 风险：LocalVarTable 顺序须声明序（z42c Locals 是 hashed map → 并行 append 表）；z42c lambda/插值未接 → C# 同源对账源须避开。
