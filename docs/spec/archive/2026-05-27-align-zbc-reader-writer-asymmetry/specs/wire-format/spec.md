# Spec: zbc/zpkg wire format — reader-writer symmetric type names

## ADDED Requirements

### Requirement: SIGS encodes ret_type as both u8 TypeTag and u32 str_idx

#### Scenario: 写入
- **WHEN** writer 序列化 IrFunction with `RetType = "int"`
- **THEN** SIGS 段输出顺序：name_idx (u32) → param_count (u16) → ret_tag (u8 = TypeTags.FromString("int") = I32) → ret_type_str_idx (u32 = pool.Idx("int")) → exec_mode (u8) → is_static (u8) → ...

#### Scenario: 读取
- **WHEN** reader 解析上述 SIGS 段
- **THEN** 输出 FuncSig with `ret_type = "int"`（来源 = ret_type_str_idx 指向的池字符串；不是 type_tag_to_str(ret_tag) 还原的 "i32"）

### Requirement: TYPE encodes field type as both u8 TypeTag and u32 str_idx

#### Scenario: 写入
- **WHEN** writer 序列化 IrClassDesc 含 field `(Name="x", Type="int")`
- **THEN** TYPE 段输出顺序：class name_idx → base_idx → fld_count (u16) → per-field: fnam_idx (u32) → type_tag (u8 = I32) → field_type_str_idx (u32 = pool.Idx("int")) → ...

#### Scenario: 读取
- **WHEN** reader 解析上述 TYPE 段
- **THEN** 输出 FieldDesc with `type_tag = "int"`（来源 = field_type_str_idx 指向的池字符串）

### Requirement: Read→Write round-trip 字节对账

#### Scenario: 全 fixture round-trip
- **WHEN** 对 freeze-zbc-v1 的 6 个 fixture 运行 `bytes_in → reader.Read(...) → writer.Write(...) → bytes_out`
- **THEN** `bytes_in == bytes_out`（字节相等，包括 STRS 池条目顺序）

### Requirement: zbc minor bump

#### Scenario: pre-1.7 zbc 加载
- **WHEN** reader 遇 zbc magic + version 1.6 文件
- **THEN** bail with message 含 "zbc minor 6 not supported (writer is at 7)"

#### Scenario: zbc 1.7 加载
- **WHEN** reader 遇 zbc magic + version 1.7 文件且按新 layout 编码
- **THEN** 解析成功

### Requirement: zpkg minor bump（联动）

#### Scenario: pre-0.8 zpkg 加载
- **WHEN** reader 遇 zpkg version 0.7 文件
- **THEN** bail with message 含 "zpkg minor 7 not supported (writer is at 8)"

## MODIFIED Requirements

**Before** (zbc 1.6 / zpkg 0.7)：
- SIGS ret_type 只 u8 TypeTag；reader 用 `type_tag_to_str(ret_tag)` 还原 lossy canonical string
- TYPE field type 只 u8 TypeTag；reader 用 `type_tag_to_str(type_tag)` 还原 lossy canonical string
- Read→Write 字节不等（已知 asymmetry）

**After** (zbc 1.7 / zpkg 0.8)：
- SIGS ret_type 是 u8 TypeTag + u32 str_idx；reader 优先 str_idx
- TYPE field type 是 u8 TypeTag + u32 str_idx；reader 优先 str_idx
- Read→Write 字节相等（CI 防线启用）

## IR Mapping

无新 IR 指令；仅 wire format 增长。

## Pipeline Steps

- [x] Lexer — 无
- [x] Parser / AST — 无
- [x] TypeChecker — 无
- [x] IR Codegen — 无（IR 形态不变；writer 输出多一个 u32）
- [x] VM interp — `read_sigs` / `read_type` 消费新字段
- [x] zbc/zpkg format — minor bump + fixture regen
