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

### zbc — 单文件字节码单元

#### Header（16 bytes，固定偏移）

```
[4 bytes]  magic     0x5A 0x42 0x43 0x00  ("ZBC\0")
[2 bytes]  version   major (u16, little-endian)
[2 bytes]  version   minor (u16, little-endian)
[2 bytes]  flags     bit field（见下表）
[6 bytes]  reserved  必须为 0
```

**flags 位域：**

| bit | 名称 | 值 | 说明 |
|-----|------|----|------|
| 0 | `STRIPPED` | 0 = full；1 = stripped | full：独立可用；stripped：需配套 zpkg 索引 |
| 1 | `HAS_DEBUG` | 0/1 | 含 `DBUG` section（源码行号映射等）|
| 2–15 | reserved | 必须为 0 | |

#### Section 通用头

每个 section 紧跟 header 依次排列，格式统一：

```
[4 bytes]  section_id   4-char ASCII tag
[4 bytes]  length       section 内容字节数（u32, little-endian）
[N bytes]  data         section 内容
```

`NSPC` section **必须是第一个**，支持不解析后续 section 的快速命名空间扫描。

#### Full mode sections（`flags.STRIPPED = 0`）

适用场景：`z42c file.z42 --emit zbc` 单文件编译；Z42_PATH 独立分发模块。

| section_id | 必需 | 说明 |
|------------|------|------|
| `NSPC` | ✅ | namespace 字符串（u16 length + UTF-8 bytes）|
| `STRS` | ✅ | string heap：所有字符串去重，引用用 u32 offset |
| `EXPT` | ✅ | export table：`STRS` offset → local_func_idx（u32 pair 数组）|
| `SIGS` | ✅ | function signature table：参数类型列表 + 返回类型 |
| `IMPT` | ✅ | import table：本文件依赖的外部符号名（`STRS` offset 列表）|
| `FBOF` | ✅ | function body offset table：`local_func_idx → u32 byte offset` in `FBDY` |
| `FBDY` | ✅ | function bodies：IR 字节码，按 `FBOF` 索引 seek 读取 |
| `DBUG` | ❌ | 调试信息（行号映射）；仅 `flags.HAS_DEBUG = 1` 时存在 |

#### Stripped mode sections（`flags.STRIPPED = 1`）

适用场景：indexed zpkg 构建时写入 `.cache/`；元数据已移至 zpkg 索引。

| section_id | 必需 | 说明 |
|------------|------|------|
| `NSPC` | ✅ | namespace 字符串（同 full mode）|
| `BSTR` | ✅ | body string heap：仅函数体内实际引用的字符串（外部调用目标、字符串字面量）|
| `FBOF` | ✅ | function body offset table（同 full mode）|
| `FBDY` | ✅ | function bodies（同 full mode）|

`EXPT` / `SIGS` / `IMPT` / `STRS` 均移至 zpkg，stripped zbc 不含这些 section。

**函数体内的跨文件调用** 使用 `BSTR` 中的字符串索引引用外部符号名（如 `"Models.User.greet"`），不含绝对地址，VM 加载时按名解析。

#### Content-stable 保证

stripped zbc 的全部内容由源文件唯一决定：

| 字段 | 决定因素 |
|------|---------|
| `NSPC` | 源码 namespace 声明 |
| `BSTR` | 源码中调用的外部符号名 |
| `FBOF` | 源码函数定义顺序 |
| `FBDY` | 源码逻辑 |

`source_hash` **不存于 zbc**，只存于 zpkg `FILE_TABLE`。相同源文件 + 相同编译器版本 → 逐字节相同的 stripped zbc。

#### 工具行为

| 场景 | flags | 行为 |
|------|-------|------|
| Z42_PATH 模块加载 | full | 读 `NSPC` + `EXPT` + `SIGS` 建立符号表 |
| zpkg indexed 懒加载 | stripped | 按 `local_func_idx` seek `FBOF` → 执行 `FBDY` |
| 命名空间快速扫描 | both | 只读 `NSPC`（固定第一个 section）|
| stripped 误放 Z42_PATH | stripped | 报错：`"x.zbc" is in stripped mode and cannot be used without its zpkg index` |
| `z42c disasm` | full | 显示 namespace / exports / signatures / IR |
| `z42c disasm` | stripped | 显示 namespace + IR bodies，函数以 `func#N` 标识；顶部注：`; stripped mode — function names in zpkg` |

---

### zpkg — 工程包（indexed 和 packed 统一封装）

#### Header

```
[4 bytes]  magic     0x5A 0x50 0x4B 0x00  ("ZPK\0")
[2 bytes]  version   major (u16)
[2 bytes]  version   minor (u16)
[1 byte]   mode      0x00 = indexed；0x01 = packed
[1 byte]   kind      0x00 = exe；0x01 = lib
[6 bytes]  reserved
```

#### Indexed zpkg sections

`mode = 0x00`：清单 + 全局索引，zbc 文件在 `.cache/` 独立存储。

| section_id | 说明 |
|------------|------|
| `GSTR` | global string heap：工程内所有符号名去重（namespace、函数名、类型名）|
| `NSIX` | namespace index：`GSTR` offset → `[file_entry_idx]`（依赖扫描用）|
| `SYIX` | symbol index：`GSTR` offset → `{file_idx, local_func_idx}`（懒加载 dispatch 入口）|
| `SIGS` | function signature table：`{file_idx, local_func_idx}` → 参数/返回类型（TypeChecker 用）|
| `FILE` | file table：`{source_path, zbc_path, source_hash}` 数组，路径均相对 project root |

`SYIX` 支持二分查找（按字符串 offset 排序），无需全量加载即可定位函数所在 zbc。

#### Packed zpkg sections

`mode = 0x01`：所有字节码内联，单文件自包含。

| section_id | 说明 |
|------------|------|
| `GSTR` | global string heap：跨所有模块去重，同一符号名只存一次 |
| `NSIX` | namespace index（同 indexed）|
| `SYIX` | symbol index：`GSTR` offset → `{module_idx, local_func_idx}`（同 indexed，dispatch 统一）|
| `SIGS` | function signature table |
| `MODU` | module table：每个原始 zbc 对应一条记录（namespace、exports、imports，引用 `GSTR`）|
| `FBOF` | function body offset table：`global_func_idx → u32 byte offset` in `FBDY` |
| `FBDY` | 所有函数体连续存储，call 时按 `FBOF` seek |

**符号去重效果：** 多个模块引用同一类型名（如 `"z42.core.Int"`）时，`GSTR` 只存一份，所有引用均为 4 字节 offset。

#### 懒加载路径（两种模式统一）

```
call "Models.User.greet"
  │
  ├─ indexed: SYIX 查找 → {file:1, local_func:0}
  │    ├─ slot[1] == Unloaded → read ".cache/.../UserMethods.zbc"
  │    │    解析 FBOF + FBDY → slot[1] = Ready
  │    └─ FBOF[0] → seek FBDY → execute
  │
  └─ packed:  SYIX 查找 → {module:1, local_func:0}
       ├─ slot[1] == Unloaded → seek FBOF[global_idx] in zpkg FBDY
       └─ execute
```

两种模式的 `SYIX` 结构完全一致，dispatch 路径统一，VM 无需区分 mode 后的执行逻辑。
