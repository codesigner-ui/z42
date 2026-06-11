# Tasks: port-z42c-interface

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-11 | 子系统锁：z42c（已释放）

- [x] IF-1 Z42InterfaceType + SymbolTable.Interfaces + 收集/基表判别
- [x] IF-2 typecheck：ResolveType/可赋性/接口调用/is·as/ToIrType
- [x] IF-3 IrGen 判别 + TSIG（类接口表 + 本地接口导出 + reader）
- [x] IF-4 ifacecheck 第 6 zbc 源（执行+byte-compare 6/6）+ 单测 + gate + 文档 + commit

## 实施记录（2026-06-11）
- 整链落地：Z42InterfaceType（Methods=MethodSymbol 表）+ SymbolTable.Interfaces + 收集 Pass A0（接口 stub 先于类——基表判别依赖）+ 类基表判别（接口→InterfaceNames，类→base）+ 可赋性 class→iface（Implements 走 base 链）+ 接口收者调用（签名→instance BoundCall→既有 VCall 路径零改动）+ IrGen 判别（base 回落 Std.Object）+ TSIG（类接口表替换恒 0 + 本地接口导出，intern 位 base 后）。
- **字节校准真相（最微妙的一条）**：接口的 IrType **按解析位点分裂**——体内局部 `IShape s` = Ref（copy tag 0x20/REGT 0x0E），**签名形参** = Unknown（REGT 0x00；C# 签名解析序怪癖）。FunctionEmitter 形参位特例。
- ifacecheck 第 6 zbc 源 byte-identical + 执行 ✓（对账 **6/6**）。
- 延后：泛型接口/static abstract/接口继承/实现完备性诊断（Q1：C# 单文件未见报错路径，挂账观察）。
