# Tasks: port-z42c-codegen — Bound → IR lowering

> 状态：🟡 进行中 | 创建：2026-06-09 | 子系统锁：z42c（顺序续作，semantics 归档后占用）
> **变更说明：** z42c.ir 从零镜像 C# IR 内存模型 + z42c.semantics 加 Codegen（FunctionEmitter/IrGen）把 Bound 树降级为 IR。
> **设计**：[design.md](design.md)（D1 集中 if-is / D2 class-per-instruction / D3 CG-1A 起点 / D4 .zasm-like dump，待 User 审批）。
> byte-identical .zbc（ZbcWriter）是后续独立 change。

## 进度概览
- [x] CG-1A 最小 lowering（IR 模型骨架 + 字面量/局部/二元算术/return → 单块）✅
- [x] CG-1B 控制流（if/while/break/continue → 多基本块）✅
- [ ] CG-1C 调用 + 字段 + 继承分派
- [ ] CG-1D new / cast / is / 数组
- [ ] CG-1E 比较 / 一元 / 逻辑短路 / 字符串拼接 / 三目 / ??
- [ ] CG-2 泛型 type-args + default_of

## CG-1A：最小 Bound → IR（单 entry 块）—— ✅ 已完成
### z42c.ir 数据模型（从零）
- [x] 1A-1 `IrType.z42`（int 常量 + Name(tag)；**映射 FromZ42Type 在 FunctionEmitter**——z42c.ir 无依赖叶子不能引用 Z42Type）
- [x] 1A-2 `TypedReg.z42`（Id/Type + Dump `%n`）
- [x] 1A-3 `IrModule.z42`（IrFieldDesc/IrClassDesc/IrBlock/IrFunction/IrModule，**叶子优先序**；集合 typed array+count；StringPool；.zasm-like Dump）
- [x] 1A-4 `IrInstr.z42`（IrInstr 基类 + **ConstI32**/ConstI64/ConstF64/ConstBool/ConstStr/ConstNull + Copy + Add/Sub/Mul/Div/Rem；虚 Dump；opcode 留 ZbcWriter）
- [x] 1A-5 `IrTerminator.z42`（RetTerm/BrTerm/BrCondTerm + ThrowTerm 占位）
- [x] 1A-6 ~~删 IrSkeleton~~ → **保留**（3 skeleton 仍 `new IrSkeleton()`；新 IR 文件同声明 `namespace Z42.IR`，namespace 存活，共存无害）
### z42c.semantics codegen（平铺 src/，非 Codegen/ 子目录）
- [x] 1A-7 `FunctionEmitter.z42`（EmitFunction + 集中 if-is EmitExpr[lit/ident/binary/assign]/EmitStmt[block/var-decl/return/expr] + Alloc/Emit/块管理 + `_toIrType`/`_primTag` 映射）
- [x] 1A-8 `IrGen.z42`（Generate：IrClassDesc 收集 + 逐函数 emit + StringPool intern → IrModule）
- [x] 1A-9 `IrDump.z42`（DumpFunc/DumpModule：源 → typecheck → IrGen → .zasm-like 文本）
- [x] 1A-10 ~~toml 加 z42c.ir 依赖~~ → **已存在**（z42c.semantics.z42.toml 既有 z42c.ir 依赖）
### 测试 + 文档
- [x] 1A-11 `tests/codegen/codegen_tests.z42`（+ toml）：8 单测（return-int / 二元 add 实例方法 / var-decl+赋值 / double·bool·string 字面量 / void 隐式 ret / module dump）
- [x] 1A-12 README 同步（z42c.ir 真实模型；z42c.semantics 加 FunctionEmitter/IrGen/IrDump）
- [x] 1A-13 验证：`xtask test compiler-z42` → **13 units 178 cases** 全绿（codegen 8）

> **🎉 CG-1A 完成：z42c 端到端 Bound → IR 跑通**（source → parse → typecheck → IrGen → .zasm-like IR 文本）。
> **踩坑**：① `fn` 是 z42 保留字（TokenKind.Fn）→ 变量名避开；② 同文件方法体引用「后定义的具体类型」的方法 → E0402，需**叶子优先排序**（区别于引用先定义的基类如 Bound 的 BoundStmt[]）；③ z42 `int`→IR `i32`（非 i64）。

## CG-1B：控制流（if/while/break/continue → 多基本块）—— ✅ 已完成
- [x] FunctionEmitter `_emitIf`（cond→br.cond→then/else，汇合 end 块；HasElse 分支）+ `_emitWhile`（entry→cond，cond br.cond→body/end，body 回边 br cond）
- [x] break/continue → `BrTerm` 到 `_breakLabels`/`_contLabels` 栈顶（`_pushLoop`/`_popLoop` + `_loopDepth`）+ `_fresh(hint)` 顺序标签
- [x] 5 单测（if-then+return / if-else 汇合 / while-break / while-continue / while-body 回边）
- [x] 验证：`xtask test compiler-z42` → **13 units 183 cases** 全绿（codegen 13）
- 注：条件用 bool 局部/参数（比较运算符 Eq/Lt 等 → CG-1E）；break/continue 后死代码指令无害（块已捕获 _curCount，超出不 dump）

## CG-1C–CG-2（后续增量，详见 design.md 增量表）
- [ ] CG-1D new/cast/is/数组；CG-1E 比较/一元/逻辑/拼接/三目；CG-2 泛型

## CG-1C：方法调用 + 字段 get/set + this —— ✅ 已完成
- [x] IrInstr 加 CallInstr（`call @f(args)`）/ VCallInstr（`vcall obj.m(args)`）/ FieldGetInstr（`field_get obj.f`）/ FieldSetInstr（`field_set obj.f, val`）
- [x] FunctionEmitter：EmitFunction 加 owner 参数 + `_fields`（实例方法自身字段名集合，仅 `!isStatic` 填）；`_lookupIdent` 裸字段→field_get(reg0)；`_emitMember`（obj.field 读）；`_emitCall`（free→call / static→call @Class.m / instance→vcall recv.m，receiver 先于 args emit）；`_emitAssign` 加裸字段/obj.field→field_set
- [x] IrGen：类方法传 owner=Z42ClassType，自由函数传 null
- [x] 7 单测（field-read this/bare / field-write / free-call / free-call+args / instance-call vcall / static-call）
- [x] 验证：`xtask test compiler-z42` → **13 units 190 cases** 全绿（codegen 20）
- 注：静态字段 StaticGet/Set 延后（typechecker 静态字段路径未定型）；void 调用恒带 dst（镜像 C#，未读）；继承方法经 vtable 名分派（VM 解析 slot），无需 codegen 特化

## 延后（design.md Deferred）
- 闭包·lambda / 异常 try-catch / native interop / static_init / 插值 / foreach 迭代器 / 调试元数据（LineTable）
- byte-identical .zbc（ZbcWriter）+ token 分配 → 独立后续 change

## 备注
- 受限写法沿用 semantics（class+虚 Dump 替 record/abstract；int 常量替 enum；typed array+count 替泛型字段；集中 if-is 替 visitor）。
- IrInstr.z42 超 500 行硬限 → 按类别拆（独立 refactor commit）。
