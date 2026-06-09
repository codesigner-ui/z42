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

### 字节基础设施（✅ 已完成）
- [x] 1A-1 `BinaryFormat/ByteWriter.z42`（可增长 int[]（每槽 0..255，规避 byte 型）+ LE WriteU8/U16/U32/I64/AsciiBytes/Str(u16+UTF8)/Patch32/AppendWriter/ToHex）
- [x] 1A-2 `BinaryFormat/ZbcFormat.z42`（ZbcVersion 1.11 + Op/Tag/ExecMode 常量 + Tag.FromName/FromIrType[**⚠ z42c IrType 序≠zbc tag 序，显式映射**]）
- [x] 1A-3 `BinaryFormat/ZbcStringPool.z42`（string[] + count，Intern→idx 插入序，0-based）
### 写入器（✅ 已完成）
- [x] 1A-4 `BinaryFormat/ZbcInstr.z42`（集中 if-is WriteInstr：ConstI32/I64/Bool/Null/Copy/Add..Rem + WriteTerm：Ret/RetVal/Br/BrCond[块索引]）
- [x] 1A-5 `BinaryFormat/ZbcWriter.z42`（Write(irm)：intern 预扫 + 全 8-section NSPC/STRS/TYPE/SIGS/IMPT/EXPT/FUNC/REGT + header+directory 组装；SIGS 含 zbc 1.11 attrCount）
### 验证 + 文档
- [x] 1A-7 `tests/zbc/zbc_tests.z42`（+toml）：`empty`（void Main(){}）→ **byte-identical 对 src/tests/zbc-format/empty/source.zbc（247 字节，zbc 1.11）逐字节** ✅
- [x] 1A-8 README（z42c.ir 加 BinaryFormat/ 段）+ IrDump.DumpZbcHex（semantics，端到端 source→IrGen→ZbcWriter→hex）
- [x] 1A-9 验证：`xtask test compiler-z42` = **14 units 全绿**（新增 zbc 单元，empty byte-identical）

> **🎉 ZW-1A `empty` byte-identical 完成**：z42c 自举 ZbcWriter 对 `void Main(){}` 输出全 247 字节逐字节复刻 C# ZbcWriter（header/directory/8 section/intern 序/ret 终结符）。验证了整套字节基础设施 + 8-section 结构 + 字符串池插入序。

> **🔴 实施发现（2026-06-10）—— byte-identical 越过 `empty` 被 DBUG 阻塞**：C# z42c 对**任何有语句体的函数**（如 `int F(){return 5;}`）emit **DBUG section**（源码行表 LineTable，flags.HasDebug=1 → 9 section / 306 字节）。z42c AST **不携 source span**（codegen 期延后项），故无法复刻 DBUG → const/算术函数无法 byte-identical。`empty` 恰因无语句 → 无 DBUG → 匹配。**ZbcInstr 的 const/算术编码已实现（镜像 C#，编译通过），但其 byte-identical 验证须等 span→LineTable→DBUG 链**（跨 syntax/semantics/ir 的前置）。下一步选项见备注。

- [ ] 1A-6 截 C# golden（const/算术）—— **阻塞于 DBUG**：待 span+LineTable+DBUG 或改走端到端执行验证

## ZW-1B–1E（后续增量，详见 design.md 增量表）
- [ ] ZW-1B 控制流+运算 opcode；ZW-1C 调用+token+字段；ZW-1D 对象+TYPE；ZW-1E REGT+端到端+float

## 延后（design.md Deferred）
- ZbcReader / 可选 section DBUG·TIDX·BLID·FRCS / native·闭包·异常 opcode / stripped+sidecar / xtask 全量对账 gate
- f64 字节重解释依赖 stdlib BitConverter（ZW-1E 前确认/补）

## 备注
- 受限写法沿用 z42c（class+虚方法 / int 常量替 enum / typed array+count / 集中 if-is）。
- byte-identical 铁律：section 顺序 + 编码原语 + 插入序 + token 分配每一处都须与 C# 完全一致。
