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

## 决策点（待 User 审批）

- **D1 ByteWriter**（可增长 byte[] + LE 位运算助手）。
- **D2 集中 if-is**（沿用 codegen D1）。
- **D3 字符串池插入序 + IMPT Ordinal sort**（确定性）。
- **D4 TokenAllocator**（插入序 intra-module index + import token）。
- **D5 验证 = golden hex 起步**（截 C# 输出）→ 端到端/全量对账后续。
- **ZW-1A 起点**：trivial 函数（const i32 + ret）byte-identical。
