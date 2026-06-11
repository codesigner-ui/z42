# Design: port-z42c-interface

> DRAFT。探针实证：接口无 TYPE 条目 / base 判别后回落 Std.Object / 分派 = VCall 短名（instance 路径零改动）。

## 链路
SymbolCollector：interface decl（ClassDecl.Kind=="interface"）→ Z42InterfaceType{Methods}→SymbolTable.Interfaces；
class 基表逐项：名在 Interfaces → InterfaceNames[]，否则 base（首个类）。
TypeChecker：ResolveType 名中 Interfaces → 接口类型；_isAssignable(class→iface)=类/base 链 InterfaceNames 含；
接口收者 `s.Area()`：Interfaces 查方法签名 → instance BoundCall（Owner=接口名）→ 既有 codegen instance 路径
（ChainHasMethod miss[接口不在 Classes]→DepIndex miss→VCall 短名 ✓ 探针字节）。
IrGen._classDesc/extractor 同判别。TSIG 类条目接口表 + 本地 ExportedInterfaceZ（方法 public+virtual+abstract，p{i} 参名——内建接口同形态）。

## Decisions
- D1 接口方法存 MethodSymbol（同类方法形态）于 Z42InterfaceType.Methods（StrMap）
- D2 可赋性单向 class→iface；iface→class 须显式 as（运行时检查——C# 同）
- D3 zbc 路径零写入器改动（探针实证）；TSIG 路径补全（类接口表 + 本地接口导出）

## Testing
单测：收集（interface 声明 + 类接口表）/可赋性（实现类→接口 ✓ 未实现 ✗）/接口调用绑定 dump。
ifacecheck 第 6 zbc 源：执行 + byte-compare 6/6。
