# Design: port-z42c-instance-import

> DRAFT。来源：C# FunctionEmitterCalls.cs 精读（instance 三段式）+ import 期 survey。

## 路径（镜像 C# EmitInstanceBoundCall）

```
instance 调用：
  1. receiver 类链（symbols 全表=local+imported，走 base 链）自有方法？
     → 是：VCall 短名（vtable 赢）；receiver ∈ imported → TrackDepNamespace(其 ns)
  2. 否（prim/unknown/链上无）：Deps.GetInstance("m$argc"→"m")
     → 命中：CallInstr(QualifiedName, [recv]+args) + TrackDepNamespace
  3. 未中：VCall fallback（现状）
```

## Decisions

### D1：守卫查 symbols 全表（local+imported 合并后）
C# ClassRegistry 含 imported（注释实证 list.IsEmpty 走 VCall）。z42c SymbolTable.Classes 已合并 → `_chainHasMethod(symbols, clsName, method)` 走 base 链即镜像。

### D2：ImportedClassNamespaces 经 IrGen 透传进 ctx
IrDump.BuildModuleD 已持 ImportedSymbols → gen.ImportedNs = imported.ClassNamespaces → ctx。VCall fallback 用 OwnerClass 查表命中即 Track。

### D3：prim receiver 吸收（typecheck）
`call on non-class` 错误分支改为：吸收 instance BoundCall（OwnerClass=rt.Name()，ret Unknown）。注意保留真正不可调用形态（void receiver?）——MVP 全吸收（C# 同松）。

## Testing
- 单测：typecheck `s.Substring(0,2)` 吸收（ErrorCount=0 + bound dump）；
- e2e 第 4 工程：StringBuilder（imported 类 VCall + DEPS 含 Std.Text）+ Substring（prim → DepIndex Call）+ 全文件 byte-compare + 直跑。
- 回归：16 units + 3/3 + 3/3 全保持。
