# Tasks: port-z42c-zbc-writer — IrModule → byte-identical .zbc

> 状态：🟡 进行中 | 创建：2026-06-09 | 子系统锁：z42c（顺序续作，codegen 归档后占用）
> **变更说明：** z42c.ir 加 BinaryFormat/，从零镜像 C# ZbcWriter，把 IrModule 写为 byte-identical .zbc。
> **设计**：[design.md](design.md)（D1 ByteWriter / D2 集中 if-is / D3 字符串池插入序 / D4 TokenAllocator / D5 golden-hex 验证，待 User 审批）。

## 进度概览
- [ ] ZW-1A 最小：header + directory + NSPC/STRS/SIGS/FUNC + const/算术/ret opcode → trivial byte-identical
- [ ] ZW-1B 控制流（Br/BrCond + 块索引）+ 比较/位/一元 opcode
- [ ] ZW-1C 调用 + TokenAllocator + IMPT/EXPT + 字段
- [ ] ZW-1D 对象/数组/is·as + TYPE section
- [ ] ZW-1E REGT + 端到端 z42vm 执行 + f64/char（BitConverter）

## ZW-1A：最小 byte-identical（trivial 函数）
### ZW-1A-0 前置：IrModule enrich（design §②，✅ 已完成）
- [x] `IrFunction` 加 `ExecMode`（默认 "Interp"）/ `ParamTypes`（SIGS 每形参类型名）/ `RegTypes`（REGT 每寄存器 IrType 标签）
- [x] `EmitContext.Alloc` + `RecordReg` 记 reg→tag（params/this 直构 TypedReg 也记，覆盖 0..MaxReg）
- [x] `FunctionEmitter`：拆 `isInstance`（有 this，驱动 reg0/paramOffset）vs `isStaticMethod`（SIGS IsStatic 字节；**自由函数 false**，对齐 C# byte-identity 发现）
- [x] `IrGen`：模块名 = 根命名空间（`cu.HasNamespace ? cu.Namespace : "main"`，对齐 C# `cu.Namespace ?? "main"`）+ free-func 传 `isInstance=false, isStaticMethod=false`
- [x] 验证：`xtask test compiler-z42` 全绿（codegen 40 例，含 module-name 改为 "main"）

### 字节基础设施
- [ ] 1A-1 `BinaryFormat/ByteWriter.z42`（可增长 byte[]+len；WriteU8/U16/U32/I64/Bytes/Str(u16+UTF8)/Patch32/ToArray；LE 位运算拆字节）
- [ ] 1A-2 `BinaryFormat/Opcodes.z42`（opcode int 常量全表 + TypeTags + SectionTags ASCII）
- [ ] 1A-3 `BinaryFormat/ZbcStringPool.z42`（list+index map，Intern→idx 插入序）
### 写入器
- [ ] 1A-4 `BinaryFormat/ZbcInstr.z42`（集中 if-is WriteInstr：ConstI32/ConstBool/ConstStr/Copy/Add..Rem + WriteTerminator：Ret/RetVal/Br/BrCond[块索引]）
- [ ] 1A-5 `BinaryFormat/ZbcWriter.z42`（Write(module)：建 STRS 池 + BuildNspc/Strs/Sigs/Func section + header + directory 组装；Patch32 回填 offset）
### 验证 + 文档
- [ ] 1A-6 截 C# z42c 对 trivial 源（`int F(){return 5;}` 等）`.zbc` golden hex（`dotnet` 跑 C# 编译器 + hex dump）
- [ ] 1A-7 `tests/zbc/zbc_tests.z42`（+toml）：header / trivial 函数 / 算术 / directory / 字符串池 golden-hex 断言
- [ ] 1A-8 README（z42c.ir 加 BinaryFormat/ 段）
- [ ] 1A-9 验证：`xtask test compiler-z42` 全绿（新增 zbc 单元）

## ZW-1B–1E（后续增量，详见 design.md 增量表）
- [ ] ZW-1B 控制流+运算 opcode；ZW-1C 调用+token+字段；ZW-1D 对象+TYPE；ZW-1E REGT+端到端+float

## 延后（design.md Deferred）
- ZbcReader / 可选 section DBUG·TIDX·BLID·FRCS / native·闭包·异常 opcode / stripped+sidecar / xtask 全量对账 gate
- f64 字节重解释依赖 stdlib BitConverter（ZW-1E 前确认/补）

## 备注
- 受限写法沿用 z42c（class+虚方法 / int 常量替 enum / typed array+count / 集中 if-is）。
- byte-identical 铁律：section 顺序 + 编码原语 + 插入序 + token 分配每一处都须与 C# 完全一致。
