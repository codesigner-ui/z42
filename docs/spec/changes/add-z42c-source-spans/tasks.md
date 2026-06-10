# Tasks: add-z42c-source-spans — span 链（AST → Bound → LineTable → DBUG）

> 状态：🟢 已完成 | 创建：2026-06-10 | 批准+实施+归档：2026-06-10 | 子系统锁：z42c（已释放）

## 进度概览
- [x] SP-1 syntax：AST 42 节点携 Span（尾参）+ Parser 构造点填入 + 调用点机械更新
- [x] SP-2 semantics：Bound 节点携 Span + binder 透传 + 诊断真实位置（删 _noSpan 用法）
- [x] SP-3 codegen：TrackLine（同行去重/basename）+ LocalVar 声明序表 → IrFunction 两表
- [x] SP-4 ir：IrLineEntry/LocalVarEntry + ZbcWriter DBUG section + HasDebug flag
- [x] SP-5 验证：`int F(){return 5;}` 全文件 byte-identical golden + xtask e2e byte-compare 步（三源 z42c vs C# diff）+ gate 全绿

## SP-1：syntax（机械大头）
- [x] 1.1 Ast.z42/Stmt.z42/Decl.z42/TypeExpr.z42：每 sealed class 加 `public Span Span;` 尾参
- [x] 1.2 Parser.z42：每构造点取首 token 行/列 + file 填 Span
- [x] 1.3 syntax 各测试直构节点补尾参；+ span 断言用例（≥2）
- [x] 1.4 验证：`xtask test compiler-z42` syntax 单元全绿（Dump 断言零变化）

## SP-2：semantics 类型检查半
- [x] 2.1 Bound.z42 各节点加 Span 尾参；TypeChecker 透传
- [x] 2.2 诊断点改真实 span；删 `_noSpan()`
- [x] 2.3 测试直构 Bound 节点补尾参；+ 诊断位置断言用例（≥1）

## SP-3：codegen
- [x] 3.1 EmitContext：`LastLine` + `TrackLine(span)`（去重 + basename）+ LineTable 增长数组 + Locals 声明序并行表
- [x] 3.2 FunctionEmitter._emitStmt 入口 TrackLine；收尾导出两表进 IrFunction
- [x] 3.3 IrGen file 名穿线

## SP-4：ir
- [x] 4.1 IrModule.z42：IrLineEntry/IrLocalVarEntry + IrFunction 两表字段
- [x] 4.2 ZbcWriter：hasDebug 检测 + flags|=0x02 + DBUG 第 9 section（u16 lineCount + 条目 + u16 varCount + 条目）+ intern（file basename + 局部名，**intern 时机对齐 C# InternPoolStrings 的 LineTable/LocalVar 段**）

## SP-5：验证
- [x] 5.1 zbc_tests：`int F(){return 5;}` 全文件 hex 对 C# 同源（`dotnet z42c.dll <src> --emit zbc` 截取）
- [x] 5.2 xtask e2e：byte-compare 步（selfcheck/callcheck/typecheck 三源 z42c vs C# 逐字节 diff）
- [x] 5.3 `xtask test compiler-z42` 全绿；README（syntax/semantics/ir）+ self-hosting.md 状态同步

## 备注
- C# 权威：FunctionEmitter.Helpers.TrackLine / BaseName；ZbcWriter.BuildDbugSection / InternPoolStrings（LineTable.File + LocalVar.Name 的 intern 位置）。
- 风险：LocalVarTable 顺序须声明序（z42c Locals 是 hashed map → 并行 append 表）；z42c lambda/插值未接 → C# 同源对账源须避开。

## 实施记录（2026-06-10）
- SP-1/2 机械量：AST 58 类（含 holder）+ Bound 25 类尾参 Span；Parser 55+ 构造点；TypeChecker 全诊断真实位（_noSpan 删除）。
- **实施发现的额外 lowering parity（byte-compare 逼出，全部镜像 C# 字节实证）**：
  ① int 字面量恒 ConstI64（tag 按类型、载荷 8B；C# FunctionEmitterExprs:47）→ codegen dump const.i32→const.i64 ×19
  ② 函数体顶层 block 不 TrackLine（C# EmitFunction 直循环子语句；嵌套 block 照常）
  ③ while 块 label = cond_/body_/end_（非 while_*；label 入 STRS）
  ④ 无显式基类 → IrClassDesc.Base="Std.Object"（TYPE base idx 非 0xFFFFFFFF）
  ⑤ module.Functions 序 = 类方法（声明序）→ 自由函数（声明序）（IrGen 两遍；SIGS 字节实证）
  ⑥ var-decl = 专用寄存器 + copy init（影响 FUNC/REGT/DBUG instrIdx；codegen 期望重生成 ×4）
  ⑦ 字符串 `+` = Add(0x10) tag str（非 StrConcat 0x85）
- LocalVarTable 序 = **RegId 升序**（C# SnapshotLocalVarTable；非声明序——设计期风险解除，hashed map 排序即确定）。
- driver 新命令 `--dump-ir`（重生成 codegen 期望 + 永久有用）。
- **验证**：`xtask test compiler-z42` = 14 units 全绿 + e2e 四向 + **byte-compare 3/3 源逐字节（selfcheck/callcheck/typecheck，含 DBUG）** + f5 golden（306B 含 DBUG）+ empty golden。spec 6 场景全覆盖。
