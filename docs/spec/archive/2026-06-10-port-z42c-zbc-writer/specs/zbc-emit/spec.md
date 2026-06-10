# Spec: z42c ZbcWriter — IrModule → byte-identical .zbc

## ADDED Requirements

### Requirement: IrModule 序列化为 byte-identical .zbc

z42c 能把 `IrModule` 写为 `.zbc` 字节，与 C# `ZbcWriter.Write` 同源输出**逐字节相同**。

#### Scenario: 文件头（ZW-1A）
- **WHEN** 写任意 module
- **THEN** 前 16 字节 = magic `5A 42 43 00` + version_major `01 00` + version_minor（当前 `09 00`）+ flags `00 00`（full）+ section_count u16 LE + reserved `00 00 00 00`

#### Scenario: trivial 函数 byte-identical（ZW-1A）
- **WHEN** 源 `int F() { return 5; }` → IrModule → `ZbcWriter.Write`
- **THEN** 输出字节 == C# z42c 同源 `.zbc` 字节（golden hex 断言）。含 NSPC + STRS + SIGS + FUNC section，FUNC 内 F 体 = `const.i32 dst=0 val=5`（opcode `00` type_tag `04` dst `0000` + i64 `05 00…`）+ `ret %0`（RetVal opcode `43` dst `0000`）

#### Scenario: section directory（ZW-1A）
- **WHEN** 写 module
- **THEN** directory 在 offset 16，每 section 12 字节（tag 4 + offset u32 LE + size u32 LE），顺序 NSPC→STRS→SIGS→FUNC（无类/无外部调用时省 TYPE/IMPT/EXPT）

#### Scenario: 字符串池插入序（ZW-1A）
- **WHEN** module 含字符串字面量（如 `return "hi"`）
- **THEN** STRS section = `u32 count + 条目表(offset+len) + 拼接 UTF-8`，顺序 = Intern 调用序（非字母序）

#### Scenario: 算术 + 局部（ZW-1A）
- **WHEN** 源 `int Add(int a, int b) { return a + b; }`
- **THEN** FUNC 内 = `add i32 dst,a,b`（opcode `10` type_tag `04` dst u16 + a u16 + b u16）+ ret，字节与 C# 同

#### Scenario: 控制流块索引（ZW-1B）
- **WHEN** 源含 if/while
- **THEN** Br/BrCond 操作数 = 目标块**索引** u16（由 label→blockIndex 映射转换），非 label 字符串

#### Scenario: 调用 token（ZW-1C）
- **WHEN** 源含 `Helper()`（本模块）
- **THEN** Call opcode `50` + func_token u32（本模块 = Functions 插入序 index）+ argc u8 + args

## IR Mapping

| IrInstr/Terminator | opcode | 操作数编码 |
|--------------------|--------|-----------|
| ConstI32Instr | 0x00 | type_tag i32 + dst + val i64 LE |
| ConstBoolInstr | 0x02 | dst + val u8 |
| ConstStrInstr | 0x03 | dst + str_idx u32 |
| CopyInstr | 0x05 | dst + src u16 |
| Add/Sub/Mul/Div/Rem | 0x10–14 | dst + a u16 + b u16 |
| Eq..Ge | 0x30–35 | dst + a u16 + b u16 |
| Not/Neg/BitNot | 0x18/15/1C | dst + src u16 |
| RetTerm(无值/有值) | 0x42 / 0x43 | — / reg in dst |
| BrTerm / BrCondTerm | 0x40 / 0x41 | target_blk u16 / true_blk + false_blk u16 |
| Call/VCall | 0x50/52 | token/method_idx + recv? + argc + args |
| FieldGet/Set | 0x60/61 | obj + field_idx u32 (+val) |
| ObjNew/IsInstance/AsCast | 0x70/71/72 | class_token + … |
| ArrayGet/Set/StrConcat | 0x82/83/85 | — |

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen —（无，消费 IrModule 产物）
- [x] **zbc emit** — 本 change 主体（ZbcWriter：IrModule → .zbc 字节）
- [ ] VM interp —（ZW-1E 端到端：z42vm 加载 z42c .zbc 执行验证）

## 测试覆盖

- z42c.ir/tests/zbc/：每 scenario golden-hex 断言（字节级）。golden 随 zbc 版本 bump 重生。
