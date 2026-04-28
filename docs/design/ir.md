# z42 Intermediate Representation (IR)

## Design Goals

- Typed SSA form — every value has an explicit type
- Register-based — maps efficiently to JIT and native codegen
- Ownership annotations preserved — enables Rust VM to enforce safety
- Compact binary serialisation — `.zbc` bytecode files (see [zbc.md](zbc.md))
- Human-readable text form — `.zasm` for debugging

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
%r = const.char '<char>'
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

### Native Interop (C1 scaffold)

```
%r = call.native     "<module>::<type>::<symbol>"(%arg0, ...)
%r = call.native.vt  %recv.<vtable_slot>(%arg0, ...)
%p = pin    %src                  # borrow String/Array buffer for FFI
     unpin  %pinned               # release pin
```

Four opcodes lock down the binary format for the L2+ three-tier ABI (see
[interop.md](interop.md)). Each is **declared** in C1 with no runtime
behaviour; subsequent specs (C2, C4, C5) wire up dispatch.

| Opcode | Byte | Operands | Filled by spec |
|--------|------|----------|----------------|
| `call.native` | `0x53` | `dst`, module:str, type:str, symbol:str, args | C2 (`impl-tier1-c-abi`) |
| `call.native.vt` | `0x54` | `dst`, recv:reg, vtable_slot:u16, args | C5 (`impl-source-generator`) |
| `pin` | `0x90` | `dst`, src:reg | C4 (`impl-pinned-block`) |
| `unpin` | `0x91` | pinned:reg (no dst) | C4 |

`call.native` is the direct-symbol path used to call functions registered
through `z42_register_type` (Tier 1 C ABI). `call.native.vt` is the
vtable-indexed path: the source generator picks `vtable_slot` at compile
time so no name lookup happens at runtime, matching C# 11+
`[LibraryImport]` semantics.

`pin` borrows the raw buffer of a `String` or blittable-element `Array<T>`
for FFI use; `unpin` returns it to normal use. Pinned regions cannot be
mutated or relocated until unpinned. The `pinned` block syntax in user
code lowers to a `pin` … `unpin` pair around the FFI call.

VM behaviour (C1): the interpreter raises a clean error
("`<opcode>` not yet implemented (Z090x, see spec C2/C4/C5)") if any of
these opcodes execute; the JIT translator refuses to compile a function
that contains them. Both behaviours flip to real implementations as the
respective specs land.

JSON wire format:
```json
{"op": "call_native",        "dst": 2, "module": "numz42", "type_name": "Tensor",
                             "symbol": "__shim_Tensor_dot", "args": [0, 1]}
{"op": "call_native_vtable", "dst": 3, "recv": 0, "vtable_slot": 7, "args": [1]}
{"op": "pin_ptr",            "dst": 4, "src": 1}
{"op": "unpin_ptr",          "pinned": 4}
```

### String Operations
```
%r = str.concat %a, %b          # concatenate two strings
%r = to_str %src                 # convert any value to its string representation
```

`to_str` converts a value to string. For `Value::Object`, it dispatches `ToString()` via the
vtable (same as `v_call %obj.ToString`). If no `ToString` override exists, it falls back to
the `__obj_to_str` builtin (unqualified type name). All other value types use the built-in
formatting directly (e.g. `I64` → decimal string, `Bool` → `"true"/"false"`).

JSON wire format:
```json
{"op": "str_concat", "dst": 3, "a": 1, "b": 2}
{"op": "to_str",     "dst": 4, "src": 1}
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
%r = obj_new  <ClassName> ctor=<CtorName>(%arg0, %arg1, ...)
                                                # allocate + call overload-resolved ctor
%r = field_get %obj, <field>                    # load a field
     field_set %obj, <field>, %value            # store a field
```

JSON wire format (tag = `"op"`):
```json
{"op": "obj_new",     "dst": 5, "class_name": "Demo.Point", "ctor_name": "Demo.Point.Point$2", "args": [1, 2]}
{"op": "field_get",   "dst": 6, "obj": 5, "field_name": "X"}
{"op": "field_set",             "obj": 5, "field_name": "X", "val": 3}
{"op": "v_call",      "dst": 7, "obj": 5, "method": "Area", "args": []}
{"op": "is_instance", "dst": 8, "obj": 5, "class_name": "Demo.Shape"}
{"op": "as_cast",     "dst": 9, "obj": 5, "class_name": "Demo.Shape"}
```

`obj_new` 调用 **TypeChecker 编译期 overload-resolve 选定的具体 ctor 函数**
（`ctor_name` 字段直查），与 `call` 指令对齐 — VM 不做名字推断。命名约定：

- 单 ctor：`{ClassName}.{SimpleName}`（无 suffix），如 `Demo.Point.Point`
- 重载：`{ClassName}.{SimpleName}${N}`（1-based 声明序），如 `Demo.Point.Point$2`
- 类无显式 ctor：`ctor_name` 取单 ctor 形式占位，VM lookup 失败时跳过 ctor
  调用（默认无参 ctor 语义）

`obj_new` 分配 `ScriptObject`（slot-indexed 字段）后用 `[this, ...args]`
调用。0.7 起 `ctor_name` 字段必备，0.6 及更早 zbc 不再被支持
（按 `.claude/rules/workflow.md "不为旧版本提供兼容"`）。

`field_get` / `field_set` use pre-computed slot indices from the `TypeDesc` registry (O(1) per
access). Virtual fields are also dispatched by `field_get` for built-in primitive types:
- `Value::Str` — `"Length"` returns `I64` (Unicode scalar count, not byte length)
- `Value::Array` — `"Length"` / `"Count"` returns `I64`
- `Value::Map` — `"Length"` / `"Count"` returns `I64`

`v_call` dispatches via the pre-computed vtable in `TypeDesc` (O(1)). The vtable is flattened
at load time: base class methods appear first, derived overrides replace the corresponding slot.

For primitive types that lack a `TypeDesc`, `v_call` dispatches to the built-in name table:
- `Value::Str` — `ToString` → `__str_to_string`, `Equals` → `__str_equals`, `GetHashCode` → `__str_hash_code`

This allows object-protocol methods (`ToString`/`Equals`/`GetHashCode`) to work uniformly on
both class instances and primitive string values.

`is_instance` returns `bool` — true if the object's runtime class is `class_name` or a subclass.
The check walks `TypeDesc.base_name` links in the pre-built registry (O(depth)).

`as_cast` returns the object unchanged if it matches `class_name` (or a subclass), else `null`.

#### ScriptObject / TypeDesc (VM internals)

At load time `build_type_registry` topologically sorts all `ClassDesc` entries and for each
class pre-computes a `TypeDesc`:

| Field | Type | Description |
|-------|------|-------------|
| `name` | `String` | Fully-qualified class name (e.g. `"Demo.Circle"`) |
| `base_name` | `Option<String>` | Direct base class name, or `None` |
| `fields` | `Vec<FieldSlot>` | Flat field list (base fields first) |
| `field_index` | `HashMap<String, usize>` | Name → slot index (O(1) lookup) |
| `vtable` | `Vec<(String, String)>` | `(method_name, qualified_func_name)` |
| `vtable_index` | `HashMap<String, usize>` | Method name → vtable slot (O(1) lookup) |

Each `ScriptObject` instance holds an `Arc<TypeDesc>` (shared across all instances of the same
class), a `Vec<Value>` of field slots, and a `NativeData` discriminant for built-in backing
stores (`NativeData::StringBuilder(String)` for `Std.Text.StringBuilder`).

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

`.zbc` 和 `.zpkg` 二进制格式的完整规范见 [zbc.md](zbc.md)。
