# Design: port-z42c-package-symbols

> DRAFT。探针实证缺口；C# sibling-exports 机制语义等价于全包合并符号表（其逐文件形态为增量服务）。

## PS-1 两趟 build
趟 1：全文件 parse → CompilationUnit[]；SymbolCollector.CollectAll（接口 stub→类 stub→成员，跨 CU 同表）+ imported merge。
趟 2：逐文件 typecheck（同一 SymbolTable）+ IrGen（per-file IrModule 不变；usings/usedDepNs per-file）。
风险：同包同名类冲突（后文件覆盖 vs C# 报错）——MVP first-wins，挂账诊断。

## PS-2 arr.Length
typecheck：BoundMember target 为 Z42ArrayType 且名 Length → int。
codegen：_emitMember 数组路径 → ArrayLenInstr(dst, arr)；编码（C# op 查表）+ REGT(dst, arr)。

## 验证
multifile（a.z42 类 + b.z42 用之 + arr.Length）双构建逐字节；z42c.core 经 z42c build 0 错产 zpkg（自举首包冒烟，不对账——C# 对照需相同 include 形态，挂账下轮）。
