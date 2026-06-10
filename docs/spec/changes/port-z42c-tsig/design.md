# Design: port-z42c-tsig — TSIG/IMPL → zpkg 全文件 byte-identical

> 状态：DRAFT（待 User 审批）｜来源：C# ZpkgWriter.Tsig.cs(232)/ExportedTypes.cs(126)/ExportedTypeExtractor.cs(365) 全量 map + /tmp/zb 同源字节解码。

## Architecture

```
driver build 每文件：
  IrDump.BuildModule(src,file) → IrModule          （已有）
  ExportedTypeExtractor.Extract(cu, symbols, ns) → ExportedModuleZ   [TS-2]
ZpkgFileZ.ExportedModules[]                         [TS-4]
ZpkgWriterZ.WritePacked：
  intern：ns → exports → deps → **InternTsigStrings(每 ExportedModule)** → 逐模块(ns/src/hash/池)   ← 时机 1:1 C#
  段序：META STRS NSPC EXPT DEPS SIGS MODS **TSIG IMPL**（9 段，仅 ExportedModules 非空时）
```

## TSIG wire（C# BuildTsigSection，z42c 子集逐字段镜像）

```
u16 modCount；每 mod：
  u32 ns_idx
  u16 classCount；每类：u32 name + u32 base(无→0xFFFFFFFF) + u8 flags(abstract1/sealed2/static4)
    + u16 fieldCount{u32 name,u32 type,u32 vis,u8 static}
    + u16 methodCount{u32 name,u32 ret,u32 vis,u8 flags(static1/virtual2/abstract4),u16 minArg,u8 paramCount{u32 pname,u32 ptype}}
    + u16 ifaceCount(0) + u8 tpCount(0) + u8 constraintCount(0)
  u16 ifaceCount(0)；u16 enumCount(0)
  u16 fnCount；每 fn：u32 name + u32 ret + u16 minArg + u8 paramCount{u32,u32} + u8 tpCount(0) + u8 constraints(0)
  u16 delegateCount(0)
IMPL：u16 modCount；每 mod：u32 ns_idx + u16 implCount(0)
```

## Decisions

### D1：提取按 CU 声明序（确定性铁律）
z42c Z42ClassType.Fields/Methods 是 hashed StrMap（迭代非确定）→ extractor **不迭代符号表**，走 CompilationUnit 声明序（类序/成员序/函数序），符号表只做类型解析查询。C# Extract 同样以 cu 为序源（其 sem 字典在 C# 是插入序 Dictionary——CU 序的等价物；z42c 直接用 CU 更显式）。

### D2：z42c 无的构造写 0 计数
接口/枚举/委托/impl/泛型 tp/约束在 z42c 可编译子集中不存在 → 各 count 字段写 0。字节上与 C# 对同源工程（不含这些构造）完全一致。前端补构造时此处随之扩展。

### D3：FQ/visibility 以同源字节校准
名字限定（类 FQ？方法短名？param type 拼写 int/string？）与 visibility 串不靠推断——实施首件 dump C# 同源 TSIG 池逐串校准（与 zbc-writer 期 SIGS 校准同法）。

### D4：IMPL 恒随 TSIG（C# 注释明示 "TSIG present ⇒ IMPL present"）
写空 impl 表保 decoder 均匀性。

## Testing Strategy
- 单测：extractor 声明序（两类两函数源 → Exported 序断言）；TSIG 空类布局 hex。
- e2e（升级 xtask 既有步）：buildapp + demo.minimal 两工程 z42c vs C#（--strip-symbols=false）全文件 cmp。
- 回归：15 units + zbc byte-compare 3/3 + build e2e 直跑全保持。

## Deferred
非空接口/枚举/委托/impl 导出；TypeParams/约束导出；消费侧（ZpkgReader/DepIndex/ImportedSymbolLoader → 独立 change）。
