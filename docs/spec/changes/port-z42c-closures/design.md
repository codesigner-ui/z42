# Design: port-z42c-closures

> DRAFT。C# 权威已全 survey（agent 报告：四指令编码/lift 命名/env reg0/末尾追加/SIGS IsStatic=1）。

## 链路
parser：`IDENT =>` 或 `( … ) =>` lookahead → LambdaExpr{params, exprBody|blockBody}
typecheck：var-decl/assign 目标为 Z42FuncType（Func<A,R> 等结构解析）→ BindLambda(expected)：
  lambda scope（参数 Define）+ frame；体内 ident 未中 lambda scope 而中外层 → BoundCapture(name,type) 去重记录，
  ident 绑为 BoundCapturedIdent(name, captureIdx)；产 BoundLambda{params, body, captures, Z42FuncType}
  `f(args)` 且 f 类型是 Z42FuncType → BoundIndirectCall
codegen：lift 名 = enclosingFQ + "__lambda_" + idx（IrGen 级计数器）；
  无捕获：EmitLifted（参数 reg0..）→ LoadFn(dst, liftName)
  有捕获：EmitLifted env 版（env=reg0、参数 reg1..；BoundCapturedIdent → array_get env[idx]）
          捕获值寄存器收集 → MkClos(dst, liftName, capRegs, stackAlloc=0)
  lift 函数收集于 IrGen._lifted，Generate 末尾追加（SIGS IsStatic=true、ExecMode Interp）
  BoundIndirectCall → callee 求值 + CallIndirect(dst, callee, args)
wire：0x55/0x56/0x57（survey 字节布局：tag=FromIrType(dst)；MkClos 带 stackAlloc u8 + capture args）

## Decisions
- D1 Func/Action/Predicate 结构解析进 ResolveTypeP（"Func"<A..,R>→Z42FuncType；"Action"<A..>→ret void；"Predicate"<T>→ret bool）——C# 经委托注册表但产物同构，MVP 零注册表
- D2 捕获引用在 typecheck 期标记（BoundCapturedIdent 含 captureIdx）——lift 发射零查表
- D3 stackAlloc 恒 0（逃逸分析延后；C# 不逃逸才 1——corpus 用逃逸形态规避或字节校准）
- D4 lambda 计数器挂 IrGen（per-enclosing-fn 计数，镜像 NextLambdaIndex）

## Testing
parser dump×2 / typecheck（推导+捕获 dump）×2 / codegen dump（LoadFn/MkClos 形态）×2 / closcheck 第 7 源执行+byte-compare 7/7。
