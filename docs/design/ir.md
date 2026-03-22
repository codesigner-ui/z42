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

## Binary Format (.z42bc)

Header:
```
[4 bytes] magic: 0x5A 0x34 0x32 0x00  ("Z42\0")
[2 bytes] version major
[2 bytes] version minor
[4 bytes] section count
[sections...]
```

Sections:
- `TYPE`  — type definitions
- `FUNC`  — function bodies
- `DATA`  — static data / string pool
- `META`  — debug info, source maps
- `XMODE` — per-function execution mode overrides
