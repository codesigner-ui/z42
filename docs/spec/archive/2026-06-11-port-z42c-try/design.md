# Design: port-z42c-try

> DRAFT。C# 权威全段已精读（lowering 形态 + 表编码 + intern 位）。

## Lowering（1:1 C# EmitBoundTryCatch）

```
br try_start_N
try_start_N: <try 体> → br try_end_N
try_end_N: br <finally_N | after_try_N>
每 catch 子句：表条目(try_start, try_end, catch_start_K, FQ 类名|null, catchReg=Alloc(Ref))
  catch_start_K: [varName 绑 catchReg] <catch 体> → br <finally|after>
无 catch 有 finally：条目(…, catch_finally, "*", reg)；catch_finally: <finally 体> → br rethrow；rethrow: throw reg
有 finally：finally_N: <finally 体> → br after_try_N
after_try_N:
throw e；求值 → ThrowTerm(reg)（块终结）
```

## Wire（FUNC 每函数，C# 实证）
`regCount u16, blockCount u16, instrLen u32, excCount u16, blockOffsets u32×, exc 条目×{tryStart u16, tryEnd u16, catchLabel u16（块号）, catchType u32(pool|0xFFFFFFFF), catchReg u16}, instrBytes`
intern：每函数块串之后、LineTable file 之前：TryStart/TryEnd/CatchLabel + CatchType(非 null)。

## Decisions
- D1 IrFunction.ExcTable 用**可变公字段**（默认空数组）——16 参 ctor 已是上限，新增走赋值（ZpkgReader stub/测试零改动）
- D2 catch 变量类型：解析 ExType 成类则该类，无类型 catch → Ref/Unknown（C# WriteBackName 绑 catchReg 即寄存器别名——z42c Locals.Put(var, catchReg) 同法）
- D3 FQ 异常类名经 QualifyClass（imported 异常类→Std.×；本地→模块 ns）

## Testing
typecheck（try/catch/throw 绑定 + catch 变量可用）/codegen dump（标签+表形态）/trycheck e2e（throw→catch 命中/类型过滤/finally 执行序+oracle）/byte-compare 5/5。
