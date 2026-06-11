# Tasks: port-z42c-closures

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-11 | 子系统锁：z42c

- [ ] CL-1 syntax：LambdaExpr/Param + 解析（两参形态/两体形态）+ 单测
- [ ] CL-2 typecheck：Func 结构解析 + BindLambda（expected 推导/捕获分析/BoundCapturedIdent）+ BoundIndirectCall + 单测
- [ ] CL-3 codegen：lift（命名/末尾追加/env 版）+ LoadFn/MkClos/CallIndirect 发射 + 单测
- [ ] CL-4 writer：三指令编码 + REGT visits
- [ ] CL-5 closcheck 第 7 zbc 源（执行+byte-compare 7/7）+ gate + 文档 + commit
