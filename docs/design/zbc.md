# z42bc 二进制格式规范

## 设计目标

1. **二进制 + 文本双形态**：`.zbc` 二进制与 `.z42asm` 文本一一对应，可互转，不丢信息
2. **解释器直接执行**：指令流即执行流，无须二次转换，解释器 fetch-decode-dispatch 即可跑
3. **JIT 直接翻译**：每条指令携带类型信息，JIT 无须重新分析类型即可生成带类型的 CLIF
4. **SSA Block 参数**（非 phi 节点）：分支携带实参，解释器传参、JIT 建立 CLIF block 参数均自然映射

---

## 核心设计决策

### 用 Block 参数替代 Phi 节点

传统 SSA phi 节点在解释器中处理笨拙（需要"在块入口提前求值"）。
z42bc 采用 **Cranelift / MLIR 风格的 Block 参数**：

```
; 旧风格（phi 节点）
loop:
  %i = phi [entry: %i0] [loop: %i_next]

; z42bc 风格（block 参数）
loop(%i: i32):
  ...
  br loop(%i_next)          ; 传递下一迭代的值
```

- **解释器**：`br block(args...)` 时把 args 写入目标 block 的参数寄存器，然后跳转
- **JIT**：Cranelift block 天然支持 block 参数，1:1 映射，无需任何转换

### 寄存器索引（u16）

每个函数最多 65 535 个虚拟寄存器，覆盖所有实际场景。
参数寄存器 = 前 N 个（`%0..%N-1`），其余为计算寄存器。

### 指令携带类型标签

每条指令 header 含 `type: u8`，JIT 直接按类型选择 CLIF 操作，无需类型传播 pass。

---

## 文本格式（`.z42asm`）

文本格式是二进制的可读投影，可由 `z42c --dump-asm` 生成，也可被 `z42c --assemble` 读回二进制。

```asm
.module "Demo.Greet"  version 0.1.0

.strings
  s0  "Hello, "
  s1  "world"

; --- 函数定义 ---
.func @greet  exported
  .params  (str)          ; %0 = name: str
  .returns str
  .regs    3

  .block entry
    %1:str = const.str  s0
    %2:str = call @str.concat  %1, %0
    ret  %2

.func @main  exported
  .params  ()
  .returns void
  .regs    3

  .block entry
    %0:str = const.str  s1
    %1:str = call @greet  %0
    call.void @io.println  %1
    ret
```

**文本语法规则**：
- 寄存器：`%N`（N 为十进制索引）
- 类型标注：`%dst:type = opcode ...`（dst 无类型时写 `-`）
- 字符串引用：`s<idx>`（引用 `.strings` 池）
- 函数引用：`@fully.qualified.name`
- 块标签：`label:` 加 block 参数 `label(%p0:type, %p1:type):`
- 跳转带参：`br label(%a, %b)`

---

## 类型标签（TypeTag, u8）

```
0x00  void
0x01  bool
0x02  i8     0x03  i16     0x04  i32     0x05  i64
0x06  u8     0x07  u16     0x08  u32     0x09  u64
0x0A  f32    0x0B  f64
0x0C  char
0x0D  str                      ; GC 管理的 UTF-8 字符串
0x10  object  (+ u16 class_idx) ; GC 管理的类实例
0x11  array   (+ u8 elem_type)  ; GC 管理的数组
0x12  struct  (+ u16 type_idx)
0x13  tuple   (+ u8 arity)
0x14  enum    (+ u16 type_idx)
0x1F  ptr     (+ u8 elem_type)  ; raw pointer（unsafe）
```

扩展类型（`0x10`—`0x1F`）在指令流中以 **类型描述词** 形式内联：首字节为标签，后续字节为参数。

---

## 操作码表（Opcode, u8）

### 常量（0x00–0x0F）

| 操作码 | 助记符     | 操作数                       | 说明 |
|--------|------------|------------------------------|------|
| 0x00   | const.i    | dst:u16, value:i64           | 整数常量（按 type 截断） |
| 0x01   | const.f    | dst:u16, value:f64           | 浮点常量 |
| 0x02   | const.bool | dst:u16, value:u8            | true=1 / false=0 |
| 0x03   | const.str  | dst:u16, str_idx:u32         | 字符串池引用 |
| 0x04   | const.null | dst:u16                      | null（与 type 一起使用） |

### 算术与逻辑（0x10–0x2F）

| 操作码 | 助记符   | 操作数            |
|--------|----------|-------------------|
| 0x10   | add      | dst, a, b         |
| 0x11   | sub      | dst, a, b         |
| 0x12   | mul      | dst, a, b         |
| 0x13   | div      | dst, a, b         |
| 0x14   | rem      | dst, a, b         |
| 0x15   | neg      | dst, a            |
| 0x16   | and      | dst, a, b         |
| 0x17   | or       | dst, a, b         |
| 0x18   | not      | dst, a            |
| 0x19   | bit_and  | dst, a, b         |
| 0x1A   | bit_or   | dst, a, b         |
| 0x1B   | bit_xor  | dst, a, b         |
| 0x1C   | bit_not  | dst, a            |
| 0x1D   | shl      | dst, a, b         |
| 0x1E   | shr      | dst, a, b         |

### 比较（0x30–0x3F，结果类型始终 bool）

| 操作码 | 助记符 | 操作数      |
|--------|--------|-------------|
| 0x30   | eq     | dst, a, b   |
| 0x31   | ne     | dst, a, b   |
| 0x32   | lt     | dst, a, b   |
| 0x33   | le     | dst, a, b   |
| 0x34   | gt     | dst, a, b   |
| 0x35   | ge     | dst, a, b   |

### 控制流（0x40–0x4F）

| 操作码 | 助记符    | 操作数                                               | 说明 |
|--------|-----------|------------------------------------------------------|------|
| 0x40   | br        | block_idx:u16, argc:u8, args:u16[]                  | 无条件跳转 + 传参 |
| 0x41   | br.cond   | cond:u16, t_idx:u16, t_argc:u8, t_args:u16[], f_idx:u16, f_argc:u8, f_args:u16[] | 条件跳转 |
| 0x42   | ret       | —                                                    | void 返回 |
| 0x43   | ret.val   | val:u16                                              | 值返回 |
| 0x44   | throw     | val:u16                                              | 抛出异常 |

### 调用（0x50–0x5F）

| 操作码 | 助记符     | 操作数                                | 说明 |
|--------|------------|---------------------------------------|------|
| 0x50   | call       | dst:u16, fn_idx:u32, argc:u8, args:u16[] | 静态调用 |
| 0x51   | call.void  | fn_idx:u32, argc:u8, args:u16[]       | 无返回值调用 |
| 0x52   | call.virt  | dst:u16, method_str:u32, recv:u16, argc:u8, args:u16[] | 虚方法调用 |
| 0x53   | call.async | dst:u16, fn_idx:u32, argc:u8, args:u16[] | async 调用，返回 task |
| 0x54   | await      | dst:u16, task:u16                     | 等待 task |

### 字段访问（0x60–0x6F）

| 操作码 | 助记符     | 操作数                          |
|--------|------------|---------------------------------|
| 0x60   | field.get  | dst:u16, obj:u16, field_idx:u16 |
| 0x61   | field.set  | obj:u16, field_idx:u16, val:u16 |
| 0x62   | static.get | dst:u16, field_str:u32          |
| 0x63   | static.set | field_str:u32, val:u16          |

### 对象（0x70–0x7F）

| 操作码 | 助记符      | 操作数                                    |
|--------|-------------|-------------------------------------------|
| 0x70   | obj.new     | dst:u16, class_idx:u16, argc:u8, args:u16[] |
| 0x71   | obj.is      | dst:u16, obj:u16, class_idx:u16           |
| 0x72   | obj.as      | dst:u16, obj:u16, class_idx:u16           |

### 数组（0x80–0x8F）

| 操作码 | 助记符    | 操作数                       |
|--------|-----------|------------------------------|
| 0x80   | arr.new   | dst:u16, len:u16             |
| 0x81   | arr.get   | dst:u16, arr:u16, idx:u16    |
| 0x82   | arr.set   | arr:u16, idx:u16, val:u16    |
| 0x83   | arr.len   | dst:u16, arr:u16             |

### Struct / Tuple（0x90–0x9F）

| 操作码 | 助记符      | 操作数                                    |
|--------|-------------|-------------------------------------------|
| 0x90   | struct.new  | dst:u16, type_idx:u16, argc:u8, args:u16[] |
| 0x91   | struct.get  | dst:u16, obj:u16, field_idx:u16           |
| 0x92   | struct.set  | obj:u16, field_idx:u16, val:u16           |
| 0x93   | tuple.new   | dst:u16, argc:u8, args:u16[]              |
| 0x94   | tuple.get   | dst:u16, tup:u16, idx:u8                  |

### Variant / Enum（0xA0–0xAF）

| 操作码 | 助记符       | 操作数                                        |
|--------|--------------|-----------------------------------------------|
| 0xA0   | var.new      | dst:u16, type_idx:u16, variant_idx:u8, payload:u16 |
| 0xA1   | var.tag      | dst:u16, val:u16                              |
| 0xA2   | var.cast     | dst:u16, val:u16, variant_idx:u8              |

---

## 指令编码布局

每条指令以 **4 字节头** 开始，后跟可变长度操作数：

```
Byte 0:  opcode  (u8)
Byte 1:  type    (u8)   — 结果/操作数类型标签；对控制流指令为 0x00
Byte 2-3: dst    (u16)  — 目标寄存器；无目标时为 0xFFFF
Byte 4+:  ...    — 按操作码定义的额外操作数字（u8 / u16 / u32 / i64）
```

所有多字节整数：**小端序（little-endian）**。

**编码示例**（`%2:i32 = add i32 %0, %1`）：
```
10          ; opcode = add
04          ; type = i32
02 00       ; dst = 2
00 00       ; src_a = 0
01 00       ; src_b = 1
```
共 8 字节。

---

## 二进制文件格式（.zbc）

### 文件头（16 字节）

```
[4]  magic:         0x5A 0x42 0x43 0x00   ("ZBC\0")
[1]  version_major
[1]  version_minor
[2]  flags          bit0=exported_entry, bit1=has_debug
[4]  section_count
[4]  reserved
```

### Section 目录（每项 16 字节）

```
[4]  tag            STRP / TYPE / FUNC / EXPO / IMPO / EXCT / META
[4]  offset         从文件头起的字节偏移
[4]  size           section 字节长度
[4]  flags
```

### STRP（String Pool）

```
[4]  count          字符串数量
entries: count × { [4] offset_in_data, [4] byte_len }
data:    UTF-8 字节流（紧密排列，无 NUL 分隔）
```

### TYPE（类型描述符）

```
[4]  count
每项:
  [1]  kind         0=class, 1=struct, 2=enum
  [4]  name_str_idx
  [2]  field_count / variant_count
  fields: { [4] name_str_idx, [1] type_tag, [2] type_param } × field_count
```

### FUNC（函数体）

```
[4]  count
每个函数:
  [4]  name_str_idx
  [4]  flags          bit0=exported, bit1=async
  [2]  param_count    前 param_count 个寄存器为入参
  [2]  reg_count      函数内虚拟寄存器总数
  [2]  block_count
  [4]  instr_count    指令流总 byte 数
  [2]  exc_count      异常表条目数

  block_table:  block_count × [4 first_instr_byte_offset, u8 param_count, u16[] param_regs]
  exc_table:    exc_count × {
    [2] try_start_block
    [2] try_end_block     ; exclusive
    [2] catch_block
    [4] catch_type_str    ; 0xFFFFFFFF = catch all
    [2] catch_reg
  }
  instr_stream: instr_count 字节的指令序列
```

### EXPO（导出表）

```
[4]  count
每项: { [4] name_str_idx, [4] func_idx, [1] kind }
      kind: 0=func, 1=class, 2=global
```

### IMPO（导入表）

```
[4]  count
每项: { [4] module_str_idx, [4] name_str_idx, [1] kind }
```

### META（调试信息，可选）

```
[4]  source_file_str_idx
[32] source_sha256
每个函数可附加:
  指令偏移 → 源文件行列映射表（压缩 delta 编码）
```

---

## 解释器执行模型

```
struct Frame {
    func:    &FuncBody,
    pc:      usize,          // 字节偏移在 instr_stream 中
    block:   u16,
    regs:    Vec<Value>,     // 大小 = reg_count
}
```

**Fetch-Decode-Dispatch 循环**：

```
loop {
    let opcode = stream[pc];   pc += 1
    let type   = stream[pc];   pc += 1
    let dst    = u16(stream[pc..]);  pc += 2
    match opcode {
        ADD  => regs[dst] = typed_add(type, regs[a], regs[b]),
        BR   => { copy block_params; block = target; pc = block_table[target] },
        CALL => push_frame(...),
        ...
    }
}
```

- **Block 参数传递**：`br target(args...)` 时，将 args 写入 `regs[target_block_params[i]]`，再跳转
- **异常处理**：`throw` 时线性搜索 exc_table（小函数中通常 0–3 项，可接受）

---

## JIT 翻译模型（Cranelift）

z42bc → Cranelift CLIF 的映射直接、无歧义：

| z42bc 概念 | Cranelift 映射 |
|-----------|---------------|
| 函数 | `FunctionBuilder` |
| Block（带参数）| `Block` + `append_block_param` |
| 寄存器（SSA）| `Value`（CLIF SSA Value）|
| TypeTag | `types::I32 / I64 / F64 / ...` |
| `br block(args)` | `ins().jump(block, args)` |
| `br.cond` | `ins().brif(cond, t_block, t_args, f_block, f_args)` |
| `call fn_idx` | `ins().call(func_ref, args)` |
| `call.virt` | 生成 vtable 查找 + 间接调用 |

**翻译流程**（无需预分析 pass）：

```
for each block in func.block_table:
    builder.switch_to_block(clif_block[block_idx])
    for each instr in block:
        let type = clif_type(instr.type)
        match instr.opcode:
            ADD  => builder.ins().iadd(lhs, rhs)
            BR   => builder.ins().jump(target, args)
            CALL => builder.ins().call(fn_ref, args)
            ...
```

因为每条指令已携带类型标签，JIT 翻译器无需任何类型推导，**完全单遍（single-pass）**。

---

## 二进制 ↔ 文本互转

```bash
# 二进制 → 文本（反汇编）
z42c --disassemble foo.zbc > foo.z42asm

# 文本 → 二进制（汇编）
z42c --assemble foo.z42asm -o foo.zbc
```

文本格式保留所有信息（寄存器编号、类型标签、字符串池、异常表），互转无损。

---

## 版本兼容性

- `version_major` 变化 → 破坏性变更，VM 必须拒绝加载
- `version_minor` 变化 → 新增操作码，旧 VM 遇到未知 opcode 报 `UnsupportedOpcode` 错误
- 当前版本：`major=0, minor=1`
