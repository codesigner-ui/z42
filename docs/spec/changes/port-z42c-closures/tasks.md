# Tasks: port-z42c-closures

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-11 | 子系统锁：z42c（已释放）

- [x] CL-1 syntax：LambdaExpr/Param + 解析（两参形态/两体形态）+ 单测
- [x] CL-2 typecheck：Func 结构解析 + BindLambda（expected 推导/捕获分析/BoundCapturedIdent）+ BoundIndirectCall + 单测
- [x] CL-3 codegen：lift（命名/末尾追加/env 版）+ LoadFn/MkClos/CallIndirect 发射 + 单测
- [x] CL-4 writer：三指令编码 + REGT visits
- [x] CL-5 closcheck 第 7 zbc 源（执行+byte-compare 7/7）+ gate + 文档 + commit

## 实施记录（2026-06-11）

**🎉 三大件收官：closcheck（无捕获 dbl + 捕获 mul + Func 形参 Apply + 间接调用）全文件 byte-identical（3402B）+ 执行正确**——gate zpkg 对账升 5/5。

链路全新建：LambdaExpr 解析（lookahead 平衡括号检测 `=>`）→ BindLambda（expected 自 var-decl 显式 Func 类型；捕获钩子在 Ident 绑定处——frame 非重入，嵌套延后）→ lift（`{CurFn}__lambda_{idx}`，子发射器自建 ctx，模块函数表尾追加）→ LoadFn 0x55/MkClos 0x57/CallIndirect 0x56 编码 + REGT。**顺带修了 2B 期挂账 syntax gap**：泛型类型注解 var-decl（`Func<int,int> f = …`/`Box<int> b = …`）的 `_isVarDeclStart` 平衡尖括号 lookahead。

**字节校准五连（全靠 dump，survey 报告也会错）**：
① C# 单文件模式解析不了 Func（委托注册表来自 import）→ corpus 走 zpkg build 路径
② lift 函数 SIGS **IsStatic=0**（survey 说 true，字节说 0——字节赢）且 **ParamTypes 全 "?"**
③ LoadFn/MkClos 目标名要 intern（C# 池序实证）；lift 子发射器需自建 ctx + **NextReg 推进过参数区**（漏则 const 撞 reg0）
④ **C# 逃逸分析对非逃逸闭包发 stackAlloc=1**——corpus 用逃逸形态（Apply 传参）规避（z42c 恒 0 维持 D3，挂账）
⑤ lift 表达式体记 **1 条 DBUG 行**（体表达式位置）；Z42FuncType 类型串 = **"Func<int, int>"** 委托拼写（", " 分隔）；Func 形参寄存器 = Unknown（接口同款签名位怪癖）
**+ prelude 又漂移**：IBasicCollection 第 4 方法 AddOne（并行流当日加）——内建表同步。

延后：方法组/LoadFnCached/FRCS、逃逸分析、嵌套捕获、lambda 直接作实参、E0502 推导诊断完备性。
