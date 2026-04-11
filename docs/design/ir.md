# z42 Intermediate Representation (IR)

## Design Goals

- Typed SSA form — every value has an explicit type
- Register-based — maps efficiently to JIT and native codegen
- Ownership annotations preserved — enables Rust VM to enforce safety
- Compact binary serialisation — `.z42bc` bytecode files
- Human-readable text form — `.z42ir` for debugging

---

## Modules & Functions

```
module "hello"

fn @greet(%name: str) -> str {
entry:
    %lit = const.str "Hello, "
    %msg = call @str.concat(%lit, %name)
    ret %msg
}

fn @main() -> void {
entry:
    %s   = const.str "world"
    %msg = call @greet(%s)
    call @io.println(%msg)
    ret
}
```

---

## Type System in IR

| IR Type    | Description                        |
|------------|------------------------------------|
| `i8..i64`  | Signed integers                    |
| `u8..u64`  | Unsigned integers                  |
| `f32 f64`  | Floating point                     |
| `bool`     | Boolean                            |
| `char`     | Unicode scalar                     |
| `str`      | Immutable UTF-8 string             |
| `ptr<T>`   | Raw pointer (unsafe)               |
| `ref<T>`   | Immutable borrow                   |
| `refmut<T>`| Mutable borrow                     |
| `own<T>`   | Owned heap value                   |
| `void`     | No value                           |

---

## Instruction Set (draft)

### Constants
```
%r = const.i32  <value>
%r = const.f64  <value>
%r = const.bool true|false
%r = const.str  "<utf8>"
%r = const.null <type>
```

### Arithmetic & Logic
```
%r = add  <type> %a, %b
%r = sub  <type> %a, %b
%r = mul  <type> %a, %b
%r = div  <type> %a, %b
%r = rem  <type> %a, %b
%r = neg  <type> %a
%r = and  bool  %a, %b       ; logical AND
%r = or   bool  %a, %b       ; logical OR
%r = not  bool  %a           ; logical NOT
%r = bit_and  i32|i64  %a, %b
%r = bit_or   i32|i64  %a, %b
%r = bit_xor  i32|i64  %a, %b
%r = bit_not  i32|i64  %a
%r = shl      i32|i64  %a, %b
%r = shr      i32|i64  %a, %b
```

### Comparison
```
%r = eq  <type> %a, %b    -> bool
%r = ne  <type> %a, %b    -> bool
%r = lt  <type> %a, %b    -> bool
%r = le  <type> %a, %b    -> bool
%r = gt  <type> %a, %b    -> bool
%r = ge  <type> %a, %b    -> bool
```

### Control Flow
```
br <label>
br.cond %cond, <true_label>, <false_label>
ret
ret %value
throw %val          # terminate block by throwing %val; handled by exception table
```

### Exception Handling (Phase 1)

Functions may carry an exception table (list of `ExceptionEntry`). Each entry:

| Field | Description |
|-------|-------------|
| `try_start` | Label of first block inside the try region |
| `try_end` | Label of first block **after** the try region (exclusive) |
| `catch_label` | Label of catch handler block |
| `catch_type` | Optional exception type string (Phase 1: ignored, catches all) |
| `catch_reg` | Register that receives the thrown value on handler entry |

`throw %val` terminates the block. The VM searches the exception table for an entry whose
`[try_start, try_end)` block range covers the current block. If found, it jumps to
`catch_label` and pre-loads `catch_reg` with the thrown value. If not found, the exception
propagates to the caller.

JSON wire format:
```json
{"op": "throw", "reg": 3}
```

Exception table row:
```json
{"try_start": "try_start_0", "try_end": "try_end_1", "catch_label": "catch_start_2", "catch_type": "Exception", "catch_reg": 4}
```

### Calls
```
%r = call @<fn>(%arg0, %arg1, ...)
%r = call.virt %vtable_slot(%receiver, %arg0, ...)
%r = call.async @<fn>(%args...)   -> task<T>
     await %task                  -> T
%r = builtin "<name>"(%arg0, ...)
```

`builtin` invokes a VM-native built-in by name (e.g. `"__println"`, `"__list_add"`).
Built-ins are registered in the VM's dispatch table and bypass the normal function call mechanism.
They are used inside stdlib stub functions and for pseudo-class operations on `Array`/`Map` values
(List/Dictionary) that cannot be dispatched via `v_call`.

**Stdlib call chain** — user code emits `call` to a fully-qualified stdlib function
(e.g. `z42.io.Console.WriteLine`); that stub function contains a `builtin` instruction
which invokes the VM-native implementation (e.g. `__println`).  The compiler resolves
stdlib call sites at build time via `StdlibCallIndex` and emits `call` instead of `builtin`
so that the VM can correctly load and link the stdlib module.

**Pseudo-class instance methods** — `List<T>` and `Dictionary<K,V>` are backed by `Array`
and `Map` VM values, not by real class instances. The compiler emits `builtin` directly for
their instance methods (e.g. `list.Add(x)` → `builtin "__list_add"`) because `v_call` cannot
dispatch on non-object values.

JSON wire format:
```json
{"op": "call",    "dst": 2, "func": "z42.io.Console.WriteLine", "args": [1]}
{"op": "builtin", "dst": 3, "name": "__list_add",               "args": [0, 1]}
```

### Memory / Ownership
```
%r = alloc  <type>              # heap allocation, returns own<T>
     drop   %owned              # explicit drop (usually inserted by compiler)
%r = borrow %owned              # ref<T>
%r = borrow.mut %owned          # refmut<T>
%r = deref  %ref                # load through borrow
     store  %refmut, %value     # store through mutable borrow
```

### Objects (Phase 1 — class instances)
```
%r = obj_new  <ClassName>(%arg0, %arg1, ...)   # allocate + call constructor
%r = field_get %obj, <field>                   # load a field
     field_set %obj, <field>, %value           # store a field
```

JSON wire format (tag = `"op"`):
```json
{"op": "obj_new",     "dst": 5, "class_name": "Demo.Point", "args": [1, 2]}
{"op": "field_get",   "dst": 6, "obj": 5, "field_name": "X"}
{"op": "field_set",             "obj": 5, "field_name": "X", "val": 3}
{"op": "v_call",      "dst": 7, "obj": 5, "method": "Area", "args": []}
{"op": "is_instance", "dst": 8, "obj": 5, "class_name": "Demo.Shape"}
{"op": "as_cast",     "dst": 9, "obj": 5, "class_name": "Demo.Shape"}
```

`obj_new` finds the constructor function `ClassName.ClassName` (if it exists) and calls it
with `[this, ...args]`. The newly allocated object is GC-managed with reference semantics.
Class descriptors in `IrModule.classes` provide the field layout for zero-initialisation.

`v_call` walks the class hierarchy (most-derived first) to dispatch the correct method override.

`is_instance` returns `bool` — true if the object's runtime class is `class_name` or a subclass.

`as_cast` returns the object unchanged if it matches `class_name` (or a subclass), else `null`.

### Static Fields
```
%r = static_get <QualifiedField>      # load module-level static field by "ClassName.fieldName"
     static_set <QualifiedField>, %v  # store module-level static field
```

JSON wire format:
```json
{"op": "static_get", "dst": 3, "field": "Demo.Counter.count"}
{"op": "static_set", "field": "Demo.Counter.count", "val": 3}
```

A module with static fields may contain a zero-argument function named
`<namespace>.__static_init__` (e.g. `"Demo.__static_init__"`). The VM calls
it once, before the entry point, to initialize all static fields.

### Structs & Tuples
```
%r = struct.new <TypeName> { field0: %v0, field1: %v1 }
%r = struct.get %s, <field>
     struct.set %s, <field>, %value
%r = tuple.new (%v0, %v1, ...)
%r = tuple.get %t, <index>
```

### Enums / Variants
```
%r = variant.new <EnumName>::<Variant>(%payload)
%r = variant.tag %v             -> u32   # discriminant
%r = variant.cast %v, <Variant> -> <PayloadType>?
```

### Execution Mode Hint
```
exec.mode interp | jit | aot    # module-level directive
```

---

## Binary Format

### zbc — 单文件字节码单元（v0.4）

当前实现版本：`major=0, minor=4`。

#### Header（16 bytes，固定偏移）

```
[4 bytes]  magic      0x5A 0x42 0x43 0x00  ("ZBC\0")
[2 bytes]  major      u16, little-endian（当前 = 0）
[2 bytes]  minor      u16, little-endian（当前 = 4）
[2 bytes]  flags      bit field（见下表）
[2 bytes]  sec_count  section 数量（u16）
[4 bytes]  reserved   必须为 0
```

**flags 位域：**

| bit | 名称 | 值 | 说明 |
|-----|------|----|------|
| 0 | `STRIPPED` | 0 = full；1 = stripped | full：独立可用；stripped：需配套 zpkg 索引 |
| 1 | `HAS_DEBUG` | 0/1 | 含 `DBUG` section（源码行号映射等）|
| 2–15 | reserved | 必须为 0 | |

#### Section Directory

Header 之后紧跟 `sec_count` 个 12 字节目录项，按顺序指向各 section：

```
[4 bytes]  tag     4-char ASCII section tag
[4 bytes]  offset  u32, section 数据在文件中的绝对字节偏移
[4 bytes]  size    u32, section 数据字节数
```

Section 数据紧跟在 Directory 之后依次存储。读取时按 Directory 随机访问，无需顺序扫描。

#### Full mode sections（`flags.STRIPPED = 0`）

适用场景：`z42c file.z42 --emit zbc` 单文件编译。

| section_id | 必需 | 说明 |
|------------|------|------|
| `NSPC` | ✅ | namespace 字符串（u16 length + UTF-8 bytes）|
| `STRS` | ✅ | string heap：count[u32] + (offset[u32]+len[u32])×count + raw UTF-8 data |
| `TYPE` | ❌ | class descriptors（无类定义时省略）|
| `SIGS` | ✅ | function signature table（见下）|
| `IMPT` | ✅ | import table：count[u32] + name_idx[u32]×count（STRS 引用）|
| `EXPT` | ✅ | export table：count[u32] + (name_idx[u32]+kind[u8])×count |
| `FUNC` | ✅ | function bodies：IR 字节码 |
| `DBUG` | ❌ | 调试信息；仅 `flags.HAS_DEBUG = 1` 时存在 |

#### SIGS section 条目格式

每个函数签名 9 bytes：

```
[4 bytes]  name_idx    u32，STRS 引用（完全限定名）
[2 bytes]  param_count u16，形参数量（含 this）
[1 byte]   ret_type    类型标签（见 TypeTags）
[1 byte]   exec_mode   执行模式（0=Interp, 1=Jit, 2=Aot）
[1 byte]   is_static   0 = instance method；1 = static method（v0.4 新增）
```

#### Stripped mode sections（`flags.STRIPPED = 1`）

适用场景：indexed zpkg 构建时写入 `.cache/`；签名/导出信息已移至 zpkg。

| section_id | 必需 | 说明 |
|------------|------|------|
| `NSPC` | ✅ | namespace 字符串（同 full mode）|
| `BSTR` | ✅ | body string heap：函数体内实际引用的字符串 |
| `FUNC` | ✅ | function bodies（同 full mode）|

`EXPT` / `SIGS` / `IMPT` / `STRS` 均移至 zpkg，stripped zbc 不含这些 section。

#### 工具行为

| 场景 | flags | 行为 |
|------|-------|------|
| `z42c file.z42 --emit zbc` | full | 写全量 zbc，含 SIGS / EXPT / IMPT |
| `z42c disasm file.zbc` | full | 输出 .zasm 文本（namespace / signatures / IR bodies）|
| 命名空间快速扫描 | both | 只读 `NSPC`（Directory 定位，O(1) seek）|

---

### zpkg — 工程包（indexed 和 packed 统一封装，v0.1）

当前实现版本：`major=0, minor=1`。

#### Header（16 bytes，固定偏移）

```
[4 bytes]  magic      0x5A 0x50 0x4B 0x00  ("ZPK\0")
[2 bytes]  major      u16（当前 = 0）
[2 bytes]  minor      u16（当前 = 1）
[2 bytes]  flags      bit field（见下表）
[2 bytes]  sec_count  section 数量（u16）
[4 bytes]  reserved   必须为 0
```

**flags 位域：**

| bit | 名称 | 说明 |
|-----|------|------|
| 0 | `PACKED` | 0 = indexed；1 = packed |
| 1 | `EXE`    | 0 = lib；1 = exe（有入口函数）|

#### Section Directory

与 zbc 格式相同：`sec_count` 个 `(tag[4]+offset[4]+size[4])` 12 字节目录项。

#### 共有 sections（indexed + packed 均有）

| section_id | 说明 |
|------------|------|
| `META` | name(u16+bytes) + version(u16+bytes) + entry(u16+bytes，lib 时 len=0) |
| `STRS` | unified string heap（格式同 zbc STRS）|
| `NSPC` | namespace list：count[u32] + name_idx[u32]×count |
| `EXPT` | export table：count[u32] + (symbol_idx[u32]+kind[u8])×count |
| `DEPS` | dependency list：count[u32] + per-dep: file_idx[u32]+ns_count[u16]+ns_idx[u32]×ns_count |

#### Indexed mode（`flags.PACKED = 0`）

| section_id | 说明 |
|------------|------|
| `FILE` | file table：count[u32] + per-file: src_idx[u32]+zbc_idx[u32]+hash_idx[u32]+export_count[u16]+export_idx[u32]×export_count |

#### Packed mode（`flags.PACKED = 1`）

| section_id | 说明 |
|------------|------|
| `SIGS` | global function signature table（格式同 zbc SIGS，跨所有模块）|
| `MODS` | per-module bodies：mod_count[u32] + 每个模块: ns_idx[u32]+src_idx[u32]+hash_idx[u32]+func_count[u16]+first_sig_idx[u32]+func_body_size[u32]+func_body[N]+type_body_size[u32]+type_body[M] |

**STRS 去重效果：** packed 模式下统一 string pool 跨所有模块去重。多个模块引用同一符号名只存一份。
