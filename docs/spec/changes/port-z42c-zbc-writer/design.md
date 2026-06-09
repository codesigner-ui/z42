# Design: z42c ZbcWriter — IrModule → byte-identical .zbc

> 状态：DRAFT（待 User 审批）｜归属：port-z42c-zbc-writer
> 前置：z42c.ir IR 内存模型完整（IrModule/IrFunction/IrBlock/IrInstr/IrTerminator）。
> 来源：会话内 Explore agent 全量 map C# `z42.IR/BinaryFormat/`（ZbcWriter + Opcodes + StringPool）+ `Tokens.cs`/`TokenAllocator.cs`。

## 范围

`IrModule → byte[]`（`.zbc` 主文件），**byte-identical vs C# ZbcWriter**。不含 ZbcReader、
可选 section（DBUG/TIDX/BLID/FRCS）、native/闭包/异常 opcode、stripped 模式。

---

## .zbc 字节格式（权威：[zbc.md](../../../design/runtime/zbc.md)；本节摘要）

### 文件头（16 字节，offset 0）
```
[0-3]   magic 'Z''B''C''\0'  = 5A 42 43 00
[4-5]   version_major u16 LE = 1
[6-7]   version_minor u16 LE = 9（随 C# bump 同步）
[8-9]   flags u16 LE（bit0 Stripped / bit1 HasDebug / bit2 SymOnly；full 模式 = 0）
[10-11] section_count u16 LE
[12-15] reserved u32 = 0
```
### Section directory（section_count × 12 字节，offset 16）
```
每 section：[0-3] tag(4 ASCII，如 "FUNC") [4-7] offset u32 LE（绝对）[8-11] size u32 LE
```
### Section 顺序（full 模式，**严格按此序**）
`NSPC → STRS → TYPE → SIGS → IMPT → EXPT → FUNC → REGT`（+ 可选 DBUG/TIDX/FRCS/BLID 延后）。

### 编码原语
- 整数：**little-endian 定宽**（u8/u16/u32/i64/f64-IEEE754，无 varint/LEB128）。
- 字符串（内联，如 NSPC）：`u16 LE 字节长 + UTF-8 字节`（无 NUL）。
- 寄存器 TypedReg：`u16 LE`（仅 Id；类型在指令 type_tag 字节）。
- 字符串池 STRS：`u32 count + count×(u32 offset_in_data + u32 byte_len) + 拼接 UTF-8 data`。

### 指令编码（4 字节头 + 操作数）
```
[0] opcode u8   [1] type_tag u8   [2-3] dst u16 LE（0xFFFF=无 dst）   [4+] operands
```
opcode 表（本 change 关心的子集，全表见 Opcodes.z42）：
```
0x00 ConstI(val:i64) 0x01 ConstF(val:f64) 0x02 ConstBool(val:u8) 0x03 ConstStr(idx:u32) 0x04 ConstNull 0x05 Copy(src:u16) 0x08 ConstChar(val:i32)
0x10 Add 0x11 Sub 0x12 Mul 0x13 Div 0x14 Rem 0x15 Neg 0x18 Not 0x19 BitAnd 0x1A BitOr 0x1B BitXor 0x1C BitNot 0x1D Shl 0x1E Shr（二元 a:u16,b:u16；一元 src:u16）
0x30 Eq 0x31 Ne 0x32 Lt 0x33 Le 0x34 Gt 0x35 Ge（a:u16,b:u16）
0x40 Br(target_blk:u16) 0x41 BrCond(true_blk:u16,false_blk:u16) 0x42 Ret 0x43 RetVal(reg in dst) 0x44 Throw(reg in dst)
0x50 Call(func_token:u32, argc:u8, args:u16[]) 0x52 VCall(method_idx:u32, recv:u16, argc:u8, args:u16[])
0x60 FieldGet(obj:u16, field_idx:u32) 0x61 FieldSet(obj:u16, field_idx:u32, val:u16)
0x70 ObjNew(class_token:u32, ctor_token:u32, argc:u8, args:u16[], type_arg_count:u8, type_args:u32[]) 0x71 IsInstance(obj:u16, class_token:u32) 0x72 AsCast(obj:u16, class_token:u32)
0x82 ArrayGet(arr:u16, idx:u16) 0x83 ArraySet(arr:u16, idx:u16, val:u16) 0x85 StrConcat(a:u16, b:u16)
```
TypeTags（type_tag 字节）：`i8=0x02 i16=0x03 i32=0x04 i64=0x05 u8=0x06… f32=0x0A f64=0x0B bool=0x01 char=0x0C str=0x0D ref/object=0x20 void/unknown=0x00`（精确表见 Opcodes.z42，核对 zbc.md）。

### 控制流 = 块索引（非 label 字符串）
Br/BrCond 操作数是**目标块的索引**（u16），非 label 字符串。ZbcWriter 先给 IrFunction 的 Blocks 建 `label → blockIndex` 映射，编码终结符时把 label 转为索引。

---

## 关键决策

### D1：字节缓冲 = `ByteWriter`（可增长 byte[] + LE 助手）
z42 无 `BinaryWriter`。建 `ByteWriter`：内部 `byte[] _buf + int _len`（倍增增长，同 parser 数组模式）+
`WriteU8(int)` / `WriteU16(int)` / `WriteU32(int)` / `WriteI64(long)` / `WriteF64(double)` /
`WriteBytes(byte[])` / `WriteStr(string)`（u16 长度 + UTF-8）/ `Patch32(pos, val)`（回填 directory offset）/ `ToArray()`。
- LE 拆字节用位运算：`WriteU32(v) = WriteU8(v&0xFF); WriteU8((v>>8)&0xFF); …`。
- **UTF-8**：z42 `string` → UTF-8 字节需 `String` 的 byte 编码 API（核查 stdlib；多数 ASCII 标识符直接 char→byte，非 ASCII 名字需真 UTF-8）。

### D2：dispatch = 集中 if-is（沿用 D1 codegen）
`ZbcInstr.WriteInstr(ByteWriter, IrInstr, ...)` 一条 `if (i is ConstI32Instr){...} else if (i is AddInstr){...} …` 链，1:1 镜像 C# `WriteInstr` switch。终结符同理 `WriteTerminator(IrTerminator)`。

### D3：字符串池插入序保留（确定性关键）
`ZbcStringPool`：`string[] _list + StrMap _index`（name→idx）。`Intern(s)`：命中返回 idx，否则 append。
**STRS 字节序 = Intern 调用序**（非字母序）。各 BuildXxxSection 按固定序 Intern → 字节确定。
**IMPT 例外**：import 名 HashSet 非确定 → 写前 **Ordinal sort**（镜像 C# `OrderBy(Ordinal)`）。

### D4：TokenAllocator（Call/ObjNew/IsInstance/AsCast 字节必需）
`FromModule(module)`：按 `module.Functions`/`Classes` 插入序建 `name→index`（0-based intra-module token）。
`ResolveMethod(name, pool)`：本模块命中→index；否则 `IMPORT_BASE(0x8000_0000) + pool.Intern(name)`。`ResolveType` 同理。
token 单遍分配（写指令前），确定性来自插入序。

### D5：验证 = golden hex（ZW-1A）→ 端到端（后续）
- **ZW-1A**：跑 C# z42c 编译 trivial 源（`int F(){return 5;}`）产 `.zbc`，截取字节为 golden hex 常量，断言 z42c `ZbcWriter.Write` 输出逐字节相同。直接验字节格式。
- **后续**：端到端（z42c 写 .zbc → z42vm 加载执行验输出）+ xtask 全量对账（z42c vs C# 同源逐字节，self-hosting.md 退出标准）。

---

## 增量计划（每增量 golden-hex 断言；逐步加 opcode + section）

| # | 内容 | 关键 |
|---|------|------|
| **ZW-1A** | ByteWriter + header + directory + NSPC + STRS + SIGS + FUNC（const/copy/算术 + ret 的 opcode 编码）→ trivial 函数 byte-identical | ByteWriter/Opcodes/ZbcStringPool/ZbcWriter/ZbcInstr 骨架；SIGS+FUNC 最小 |
| **ZW-1B** | 控制流 opcode（Br/BrCond + 块索引映射）+ 比较/位/一元 opcode | label→blockIndex；Ret/RetVal/Throw 终结符 |
| **ZW-1C** | 调用（Call/VCall）+ TokenAllocator + IMPT/EXPT section + 字段（FieldGet/Set）| token 分配 + import sort |
| **ZW-1D** | 对象（ObjNew + type_args）+ 数组 + is/as + **TYPE section**（类描述符）| class_token + type_arg 编码 |
| **ZW-1E** | REGT section（寄存器类型表）+ 端到端 z42vm 执行验证 + f64/char 字面量（需 BitConverter）| float-bits 依赖 |
| **defer** | DBUG/TIDX/BLID/FRCS 可选 section / native·闭包·异常 opcode / stripped+sidecar / xtask 全量对账 gate | — |

---

## Testing Strategy

- 每增量：源 → SymbolCollect+TypeCheck+IrGen → `ZbcWriter.Write(module)` → 字节 → **golden hex 断言**（截 C# 同源输出）。
- 起步 `z42c.ir/tests/zbc/` 新 unit。golden 字节随 zbc 版本 bump 重生（regen 流程）。
- ZW-1E 起补端到端（z42vm 加载 z42c .zbc 执行）。

---

## Deferred / 不在本设计内

- ZbcReader（.zbc→IrModule）；可选 section DBUG/TIDX/BLID/FRCS；native interop/闭包/异常 opcode；stripped+.zsym sidecar；xtask 全量逐字节对账 gate（self-hosting.md 退出标准，最终接入）。
- f64 字节重解释依赖 stdlib `BitConverter`（ZW-1E 前确认/补）。

## 🔴 实施发现（2026-06-09，开工解码 golden fixture）—— scope 扩张，User 已裁决"全 8-section"

解码 ground-truth golden `src/tests/zbc-format/{empty,strp-func-minimal}/source.zbc`（C# driver `dotnet z42c.dll <src> --emit zbc -o <out>` 可重生）后两点修正：

**① 恒 8 section**（非"省略 TYPE/IMPT/EXPT"）：full 模式无条件写 `NSPC/STRS/TYPE/SIGS/IMPT/EXPT/FUNC/REGT`（空内容也在）。`empty`（`void Main(){}`）= 8 section / 245 字节。DBUG 仅 HasDebug flag 时（`empty` 无）。

**② 前置依赖：IrModule 须先 enrich**。port-z42c-codegen 刻意延后的 4 样恰是 .zbc 必需（都可合成）：

| .zbc 需要 | z42c IrModule 现状 → 补法 |
|---|---|
| 模块名 `"main"`（NSPC/SIGS）| 硬编码 `"z42c"` → IrGen 取根命名空间（无则文件/Main 推导，对齐 C#）|
| `ExecMode`（SIGS 每函数 1 字节，默认 "Interp"）| 无字段 → IrFunction 加 ExecMode，默认 "Interp" |
| `ParamTypes`（SIGS 每形参 1 类型名 str_idx）| 无字段 → EmitFunction 已解析，存进 IrFunction |
| `RegTypes`（REGT section 每寄存器 IrType 字节）| 无字段 → EmitContext.Alloc 记 reg→tag 表，存进 IrFunction |

**③ SIGS 精确布局**（每函数）：name_idx u32 + ParamCount u16 + RetType_tag u8 + RetType_str_idx u32 + ExecMode u8 + IsStatic u8 + [ParamCount × param_type_str_idx u32] + tpCount u8 + [type param + 约束]。
**④ FUNC 精确布局**（每函数，LineTable 已移 DBUG）：regCount u16 + blockCount u16 + instrBytesLen u32 + excCount u16 + [blockOffsets u32×blockCount] + [异常表] + instrBytes。
**⑤ STRS intern 序**（`empty` = `main/?/Main/void/entry`）：C# `InternPoolStrings` 预扫（module.Name → const.str 池 → 类 → 函数名/ret/param[缺省"?"] → block label …）+ 各 BuildXxxSection `pool.Idx` 取已 intern 的 idx。须 1:1 复刻。

**修正后 ZW-1A = IrModule enrich + 全 8-section writer + 精确 intern 序**，对 `empty`/`strp-func-minimal` golden 逐字节。验证用现成 fixtures（完美 oracle），随 zbc 版本 bump regen。

### `empty`（`void Main(){}`）逐字节解码（静态读 source.zbc，245 字节）

确认的精确布局（已核对字节）：
```
Header(0-15):  5A 42 43 00 | major=01 00 | minor=09 00 | flags=00 00 | secCount=08 00 | reserved=00000000
Directory(16-111, 8×12): tag(4)+offset(u32)+size(u32)，序 NSPC/STRS/TYPE/SIGS/IMPT/EXPT/FUNC/REGT
NSPC(112): len u16=4 + "main"
STRS(118): count u32=5 + 5×(off u32+len u32) + data。**池序 = main / ? / Main / void / entry**
TYPE(180): count u32=0（无类）
SIGS(184, 18B): count u32=1；fn[0]Main: name_idx u32=2("Main") + ParamCount u16=0 + RetTag u8=00(void)
               + RetIdx u32=3("void") + ExecMode u8=00(Interp) + IsStatic u8=00 + tpCount u8=0
IMPT(202): count u32=0
EXPT(206, 9B): count u32=1 + 每 export(name_idx u32=2"Main" + kind u8=0)。导出 = entry/public 函数
FUNC(215, 22B): count u32=1；Main: regCount u16=0 + blockCount u16=1 + instrBytesLen u32=4 + excCount u16=0
              + blockOffsets[1] u32=0 + instrBytes=「ret」= 42 00 FF FF（Ret op + Unknown tag + NoReg 0xFFFF）
REGT(237, 8B): func count u32=1；Main: regCount u32=0（无寄存器）
```
**全 245 字节逐一对上 ✓**（已静态核验）。终结符编码：`Ret`=op `0x42`+tag `00`+dst `FF FF`；`RetVal`=`0x43`+tag+reg；`Br`=`0x40`+`00`+`FFFF`+target_blk u16；`BrCond`=`0x41`+condtag+condreg+true_blk+false_blk。NoReg=`0xFFFF`。指令头恒 = op u8 + type_tag u8 + dst u16。EXPT kind: 0=func。REGT 每函数 = regCount u32 + regTypes 字节（fast-path 直写 fn.RegTypes；regCount=max(MaxReg,ParamCount)）。

**🔴 关键 byte-identity 发现（已验证）**：
- **自由函数 IsStatic = 0**（SIGS 字节确证）！free func 有"无 this"（paramOffset=0）**但 IsStatic 标志=false**。须区分两个概念：**hasThis**（驱动 reg0=this / paramOffset；instance=true，static method/free func=false）vs **IsStatic 标志**（SIGS 字节；仅 static class method=true，**free func=false**）。z42c 现 codegen 把 free func 传 `isStatic=true` 把两者混了 → IsStatic 字节会写成 1，**对不上 C# 的 0**。enrich 修正：IrFunction.IsStatic = isStaticMethod（free func 为 false），paramOffset 另由 hasThis 驱动。
- ExecMode Interp = `0x00`；void TypeTag = `0x00`；name_idx/retIdx 是 **0-based** 池索引；ExecMode/IsStatic/tpCount 各 1 字节紧跟 RetIdx。
- 字符串池 0-based、插入序（InternPoolStrings 预扫：module.Name → const.str 池 → 类 → 函数[name/ret/param 缺省"?"] → block label）。`empty` 的 "?" 在 "Main" 前 = ParamTypes 的缺省占位 intern 时机。

## 决策点（待 User 审批）

- **D1 ByteWriter**（可增长 byte[] + LE 位运算助手）。
- **D2 集中 if-is**（沿用 codegen D1）。
- **D3 字符串池插入序 + IMPT Ordinal sort**（确定性）。
- **D4 TokenAllocator**（插入序 intra-module index + import token）。
- **D5 验证 = golden hex 起步**（截 C# 输出）→ 端到端/全量对账后续。
- **ZW-1A 起点**：trivial 函数（const i32 + ret）byte-identical。
