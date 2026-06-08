# Tasks: port-z42c-codegen — Bound → IR lowering

> 状态：🟡 进行中 | 创建：2026-06-09 | 子系统锁：z42c（顺序续作，semantics 归档后占用）
> **变更说明：** z42c.ir 从零镜像 C# IR 内存模型 + z42c.semantics 加 Codegen（FunctionEmitter/IrGen）把 Bound 树降级为 IR。
> **设计**：[design.md](design.md)（D1 集中 if-is / D2 class-per-instruction / D3 CG-1A 起点 / D4 .zasm-like dump，待 User 审批）。
> byte-identical .zbc（ZbcWriter）是后续独立 change。

## 进度概览
- [ ] CG-1A 最小 lowering（IR 模型骨架 + 字面量/局部/二元算术/return → 单块）
- [ ] CG-1B 控制流（if/while/break/continue → 多基本块）
- [ ] CG-1C 调用 + 字段 + 继承分派
- [ ] CG-1D new / cast / is / 数组
- [ ] CG-1E 比较 / 一元 / 逻辑短路 / 字符串拼接 / 三目 / ??
- [ ] CG-2 泛型 type-args + default_of

## CG-1A：最小 Bound → IR（单 entry 块）
### z42c.ir 数据模型（从零）
- [ ] 1A-1 `IrType.z42`（int 常量 + FromZ42Type 映射[int→I64 等] + Name(tag)）
- [ ] 1A-2 `TypedReg.z42`（Id/Type + Dump `%n`）
- [ ] 1A-3 `IrModule.z42`（IrModule/IrClassDesc/IrFieldDesc/IrFunction/IrBlock；集合 typed array+count；StringPool）
- [ ] 1A-4 `IrInstr.z42`（IrInstr 基类 + ConstI64/ConstF64/ConstBool/ConstStr/ConstNull + CopyInstr + Add/Sub/Mul/Div/Rem；虚 Op()/Dump()）
- [ ] 1A-5 `IrTerminator.z42`（IrTerminator 基类 + RetTerm/BrTerm/BrCondTerm；ThrowTerm 占位）
- [ ] 1A-6 删 `IrSkeleton.z42`（先确认 pipeline 不再引用 `Z42.IR` 占位；若引用则迁移）
### z42c.semantics codegen
- [ ] 1A-7 `Codegen/FunctionEmitter.z42`（状态 + EmitFunction 入口 + EmitExpr[lit/ident/binary/assign] + EmitStmt[block/var-decl/return/expr] + Alloc/Emit/块管理）
- [ ] 1A-8 `Codegen/IrGen.z42`（Generate：IrClassDesc 收集 + 逐函数 emit + StringPool → IrModule）
- [ ] 1A-9 `IrDump.z42`（DumpFunc/DumpModule：源 → typecheck → IrGen → .zasm-like 文本）
- [ ] 1A-10 `z42c.semantics.z42.toml` 加 z42c.ir 依赖
### 测试 + 文档
- [ ] 1A-11 `tests/codegen/codegen_tests.z42`（+ toml）：return-lit / 二元算术 / var-decl+赋值 / double·bool·string 字面量 / void 隐式 ret（≥5 单测，多行 IR 文本断言）
- [ ] 1A-12 README 同步（z42c.ir 去骨架；z42c.semantics 加 Codegen/+IrDump）
- [ ] 1A-13 验证：`xtask test compiler-z42` 全绿（新增 codegen 单元）

## CG-1B–CG-2（后续增量，详见 design.md 增量表）
- [ ] CG-1B 控制流；CG-1C 调用+字段；CG-1D new/cast/is/数组；CG-1E 比较/一元/逻辑/拼接/三目；CG-2 泛型

## 延后（design.md Deferred）
- 闭包·lambda / 异常 try-catch / native interop / static_init / 插值 / foreach 迭代器 / 调试元数据（LineTable）
- byte-identical .zbc（ZbcWriter）+ token 分配 → 独立后续 change

## 备注
- 受限写法沿用 semantics（class+虚 Dump 替 record/abstract；int 常量替 enum；typed array+count 替泛型字段；集中 if-is 替 visitor）。
- IrInstr.z42 超 500 行硬限 → 按类别拆（独立 refactor commit）。
