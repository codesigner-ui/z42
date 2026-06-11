# Design: port-z42c-statics-arrays

> DRAFT。探针字节实证（/tmp/sa_cs.zbc 全解码）。

## Wire（实证）
- StaticGet 0x62: tag(dst)+dst u16+field u32（池串 "Sev.Warn" = Qualify(类)+"."+字段）
- StaticSet 0x63: tag(val)+NoReg+field u32+val u16
- ArrayNew 0x80: tag(dst=0x20)+dst u16+size u16+elemTag u8
- __static_init__: 名 `{文件stem}.__static_init__`，**函数表首位**（fn0），ret void 0 参；体 = 逐字段 ConstI64(init)→StaticSet；隐式 Ret。VM 加载期自动执行（DepIndex 跳过其名）。

## 链路
parser：`new` 后类型紧随 `[` → ArrayNewExpr(elem, size)；
typecheck：BoundArrayNew(Z42ArrayType(elem))；裸类名 `.field`（非调用）→ ct.Fields 静态查 → BoundStaticGet(类短名, 字段, 类型)；
emit：ArrayNewInstr(dst Ref, size, elemTag=FromIrType(elem))；StaticGetInstr(dst, Qualify(cls)+"."+field)；
IrGen：Generate 首段收集 CU 静态 init → FunctionEmitter.EmitStaticInit(stem) 产 fn → funcs[0]。

## Testing
单测（typecheck 数组/静态 dump + codegen dump）；sacheck e2e（探针程序）；core 冒烟 gate 步。
