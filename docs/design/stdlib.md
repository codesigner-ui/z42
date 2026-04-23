# z42 Standard Library Architecture

> **Status:** Design (L2 implementation target — M7)
>
> This document defines the three-layer standard library architecture: VM intrinsics,
> platform abstraction, and script-level BCL. It is the authoritative contract for
> M7 implementation work.

---

## Overview

The z42 standard library is organized into three layers:

```
┌─────────────────────────────────────────────────────────┐
│  Layer 3 — Script BCL  (src/libraries/*.z42)            │
│  z42 source code; user-facing APIs; overloads/docs      │
├─────────────────────────────────────────────────────────┤
│  Layer 2 — Platform HAL  (src/runtime/src/platform.rs)  │
│  Rust trait; abstracts OS/WASM/embedded differences     │
├─────────────────────────────────────────────────────────┤
│  Layer 1 — VM Intrinsics  (src/runtime/src/interp/)     │
│  Rust functions; exposed to z42 via `Builtin` IR instr  │
└─────────────────────────────────────────────────────────┘
```

---

## Design Philosophy

The z42 standard library takes the best from three reference languages:

| Source | What we borrow |
|--------|----------------|
| **C#** | Naming conventions (`PascalCase` throughout), BCL module structure (`Console`, `File`, `Path`, `StringBuilder`, `Environment`), `IEquatable`/`IComparable` protocols, LINQ-style collection methods (L3) |
| **Rust** | Trait-based protocol design (L3: `IEquatable` → `Trait`), `Result<T,E>` for error-returning I/O APIs (L3), iterator chaining, Platform HAL as a trait so the VM is host-agnostic |
| **Python** | "Batteries included" — common tasks (file I/O, string ops, math, assertions) require no third-party packages; `assert` is a first-class tool; module names are short and readable |

**Precedence rule when languages conflict:** C# wins on naming and structure; Rust wins on abstraction design; Python wins on defaults and ergonomics.

### Script-First Placement Rule

> **见 `philosophy.md` §8 "Script-First, Performance-Driven Specialization" 的权威表述。**

新增一个 stdlib 方法时按此顺序决定落点：

1. **Layer 3（z42 源码）先写** — 除非以下条件之一成立
2. **Layer 1（VM builtin）只有在**：
   - 必须访问 OS / platform / native 资源（文件 / 时间 / 内存）；或
   - 有测量依据的性能热点，且**没有对应的 IR 指令**可以 codegen 特化
3. **Codegen 特化（L3-G4b 模式）**：如果 primitive 调用能直接映射到 IR 指令
   （算术、比较、短路等），在 IrGen 识别并替换为 IR 指令，**不**经过 VM
   dispatch，也**不**新增 builtin

**例子：**

| 调用 | 落点 | 理由 |
|------|------|------|
| `x.op_Add(y)` on int/long/double | Codegen 特化 → `AddInstr` | IR 已有，对 primitive 零开销 |
| `x.CompareTo(y)` on int | VM builtin `__int_compare_to` | 无对应 IR 指令；相对少见，builtin OK |
| `"abc".Substring(1, 2)` | VM builtin `__str_substring` | 字符串操作需要 Rust UTF-8 处理 |
| `new List<T>().Add(x)` | Layer 3（z42 ArrayList 源码）| 能用脚本实现；无特殊性能需求 |
| `Math.Sqrt(x)` | VM builtin `__math_sqrt` | 需要 libm 的 native 精度 |
| `File.ReadAllText(path)` | VM builtin → Platform HAL | 必须走 OS API |

**反例**（不推荐走 builtin）：
- `List<T>.Count` — 脚本字段读取即可
- 用户类的 equality / comparison — 脚本实现，不要为每个用户类加 builtin
- 泛型算法（Max / Min / Sum）— 走约束 + 脚本

---

## Layer 1 — VM Intrinsics

Functions that cannot be implemented in z42 because they require direct Rust/OS access.
Exposed to script code via the `Builtin` IR instruction (`__name` convention).

**File layout:**

```
src/runtime/src/interp/
├── builtins/
│   ├── mod.rs          ← dispatch entry: exec_builtin(name, args)
│   ├── string.rs       ← __str_* (substring, contains, split, …)
│   ├── io.rs           ← __println, __print, __readline (→ Platform HAL)
│   ├── collections.rs  ← __list_*, __dict_*
│   ├── math.rs         ← __math_abs/max/min/pow/sqrt/…
│   └── convert.rs      ← __int_parse, __long_parse, __double_parse, __to_str
```

**Naming convention:** all intrinsic names start with `__`, are lowercase with underscores.

**Current intrinsics (L1):**

| Category    | Names |
|-------------|-------|
| I/O         | `__println`, `__print` |
| String      | `__concat`, `__len`, `__str_substring`, `__str_contains`, `__str_starts_with`, `__str_ends_with`, `__str_index_of`, `__str_replace`, `__str_to_lower`, `__str_to_upper`, `__str_trim`, `__str_trim_start`, `__str_trim_end`, `__str_split`, `__str_is_null_or_empty`, `__str_is_null_or_whitespace`, `__str_join`, `__str_concat`, `__str_format` |
| Collections | `__contains`, `__list_add`, `__list_remove_at`, `__list_clear`, `__list_insert`, `__list_sort`, `__list_reverse`, `__dict_contains_key`, `__dict_remove`, `__dict_keys`, `__dict_values` |
| Math        | `__math_abs`, `__math_max`, `__math_min`, `__math_pow`, `__math_sqrt`, `__math_floor`, `__math_ceiling`, `__math_round` |
| Convert     | `__int_parse`, `__long_parse`, `__double_parse`, `__to_str` |
| Assert      | `__assert_eq`, `__assert_true`, `__assert_false`, `__assert_contains`, `__assert_null`, `__assert_not_null` |

**Adding a new intrinsic requires changes in three places (enforced rule):**
1. `builtins/<module>.rs` — implementation
2. `builtins/mod.rs` — dispatch branch
3. `src/compiler/z42.Compiler/TypeCheck/BuiltinTable.cs` — type signature

---

## Layer 2 — Platform HAL

A Rust trait that abstracts platform-specific operations. The VM holds a `Box<dyn Platform>`
at startup, allowing the same bytecode to run on different hosts.

**File:** `src/runtime/src/platform.rs`

```rust
pub trait Platform: Send + Sync {
    // I/O
    fn stdout_write(&self, s: &str);
    fn stderr_write(&self, s: &str);
    fn stdin_readline(&self) -> Result<String>;

    // File system
    fn file_read_text(&self, path: &str) -> Result<String>;
    fn file_write_text(&self, path: &str, content: &str) -> Result<()>;
    fn file_append_text(&self, path: &str, content: &str) -> Result<()>;
    fn file_exists(&self, path: &str) -> bool;
    fn file_delete(&self, path: &str) -> Result<()>;
    fn dir_create(&self, path: &str) -> Result<()>;
    fn dir_exists(&self, path: &str) -> bool;

    // Environment
    fn env_get(&self, key: &str) -> Option<String>;
    fn env_args(&self) -> Vec<String>;

    // Time
    fn time_now_ms(&self) -> i64;   // milliseconds since Unix epoch

    // Process
    fn process_exit(&self, code: i32) -> !;
}
```

**Implementations:**

| Struct | Target |
|--------|--------|
| `NativePlatform` | Desktop (std::fs, std::io, std::env) — default |
| `WasmPlatform` | WASM/browser host (future, L3+) |

Platform operations are surfaced to z42 scripts via dedicated intrinsics in `builtins/io.rs`
that call `vm.platform.<method>()` rather than using `std::` directly.

---

## Layer 3 — Script BCL

z42 source files in `src/libraries/`. Each module:
- Is a standalone z42 project with its own `.z42.toml`
- Declares native bindings using `[Native("__intrinsic_name")]` attribute
- Compiles to `.zbc` and is loaded by the VM before user code runs

### `[Native]` Attribute Syntax

Marks a method as a VM intrinsic binding. The method body must be omitted (no `{}`).

```z42
[Native("__println")]
public static void WriteLine(string value);
```

Rules:
- Only valid on `static` methods (L2). Instance native methods are L3.
- Return type and parameter types must be verifiable by the type checker.
- The intrinsic name must exist in the VM's builtin registry; a compile-time error
  is raised if it does not.
- A class may freely mix `[Native]` methods with normal z42 methods (overloads,
  convenience wrappers, etc.).

### Module Auto-load Policy

`z42.core` is the **implicit prelude** — the default dependency of every z42 program.

| Package | Namespace | Load condition | Analogy |
|---------|-----------|---------------|---------|
| `z42.core` | `Std` | **Always loaded at VM startup.** No `using` required. | Python `builtins`, Rust `std::prelude` |
| `z42.io` | `Std.IO` | Loaded when `using Std.IO;` is present | C# `System.IO` |
| `z42.collections` | `Std.Collections` | Loaded when `using Std.Collections;` is present | C# `System.Collections.Generic` |
| `z42.text` | `Std.Text` | Loaded when `using Std.Text;` is present | C# `System.Text` |
| `z42.math` | `Std.Math` | Loaded when `using Std.Math;` is present | C# `System.Math` |

**`z42.core` auto-load semantics:**
- Loaded before the first instruction of any user module is executed.
- All names exported by `Std` (`z42.core`) are available in every file without qualification.
- It is a compile-time error to redeclare a top-level name that shadows a `Std` export without an explicit alias (L3).
- User projects **must not** declare `z42.core` as an explicit dependency in their `.z42.toml`; it is injected automatically by the compiler and VM.

### stdlib Search Path (VM)

When the VM needs to load a stdlib module it searches in order:
1. `$Z42_LIBS` environment variable (if set and directory exists)
2. `<vm-binary-dir>/../libs/`  (adjacent layout: `artifacts/z42/bin/z42vm` → `artifacts/z42/libs/`)
3. `<cwd>/artifacts/z42/libs/`  (development: `cargo run` from project root)

Each path is expected to contain files named `<module-name>.zbc` or `<module-name>.zpkg`.
Both formats are accepted; `.zpkg` (packed mode) is preferred when both exist as it
carries version metadata.

**Producing the `libs/` directory:** run `scripts/package.sh` from the project root.
This builds the VM binary and populates `artifacts/z42/libs/` with stdlib artifacts.
Until M7 (`[Native]` attribute support), the `.zbc`/`.zpkg` files are placeholders.

---

## Module Catalog

### `z42.core` — Foundation (Default Dependency)

No platform dependency. Provides base protocols and type conversion helpers.
**Always loaded at VM startup; implicit default dependency of every z42 project.**
User `.z42.toml` files must NOT declare it — it is injected automatically.

```
src/libraries/z42.core/src/
├── Object.z42        # ToString, Equals, GetHashCode stubs
├── Convert.z42       # Convert.ToInt32, Convert.ToDouble, Convert.ToString
├── Assert.z42        # Assert.Equal, Assert.True, Assert.Null, …
├── IEquatable.z42    # interface IEquatable<T>  (L3 generics required)
├── IComparable.z42   # interface IComparable<T> (L3 generics required)
└── IDisposable.z42   # interface IDisposable
```

`Convert` and `Assert` replace the current pseudo-class implementations once the
`[Native]` attribute is supported by the compiler.

### `z42.io` — Input / Output

Depends on Platform HAL.

```
src/libraries/z42.io/src/
├── Console.z42       # Console.Write, WriteLine, ReadLine
├── File.z42          # File.ReadAllText, WriteAllText, Exists, Delete
├── Path.z42          # Path.Join, GetExtension, GetFileName, GetDirectory
└── Environment.z42   # Environment.GetEnvironmentVariable, GetCommandLineArgs
```

`Console` provides typed overloads (int, bool, double, …) as pure z42 methods
wrapping the native `string` overload.

### `z42.collections` — Collections

> **Note:** `List<T>` and `Dictionary<K,V>` require generics (L3).
> Until L3 is available these types remain as pseudo-class (compiler-internal).
> This module provides non-generic helpers and is a placeholder for the L3 migration.

```
src/libraries/z42.collections/src/
├── List.z42          # placeholder; full impl deferred to L3
├── Dictionary.z42    # placeholder; full impl deferred to L3
├── Queue.z42
├── Stack.z42
└── HashSet.z42
```

### `z42.text` — Text Utilities

```
src/libraries/z42.text/src/
├── StringBuilder.z42  # Append, AppendLine, ToString, Clear
└── Regex.z42          # L3 (depends on lambda/delegate)
```

### `z42.math` — Extended Math

```
src/libraries/z42.math/src/
└── Math.z42           # Constants PI/E, Clamp, Log, Sin/Cos/Tan, …
```

Thin z42 wrappers over `__math_*` intrinsics plus pure-z42 helpers.

---

## Relationship to Pseudo-class Strategy

Currently `Console`, `Math`, `Assert`, `Convert`, `String.*` and collections are handled
via the compiler's pseudo-class mechanism (`BuiltinTable.cs`).

The migration plan:

| Component | L1/L2 (now) | L2 M7 (after `[Native]`) | L3 |
|-----------|-------------|--------------------------|-----|
| `Console` | pseudo-class | `z42.io.Console` (native binding) | — |
| `Math` | pseudo-class | `z42.math.Math` (native binding) | — |
| `Assert` | pseudo-class | `z42.core.Assert` (native binding) | — |
| `Convert` | pseudo-class | `z42.core.Convert` (native binding) | — |
| `String.*` | pseudo-class | `z42.core` / string instance methods | — |
| `List<T>` | pseudo-class | pseudo-class (unchanged) | `z42.collections` |
| `Dictionary<K,V>` | pseudo-class | pseudo-class (unchanged) | `z42.collections` |

When `z42.io.Console` is available and loaded, the compiler must prefer the script
definition over the pseudo-class fallback. Pseudo-class entries are treated as a
**fallback only** when no stdlib is loaded.

---

## Implementation Checklist (M7)

- [ ] Refactor `builtins.rs` → `builtins/` submodule layout
- [ ] Add `Platform` trait to `src/runtime/src/platform.rs`
- [ ] Implement `NativePlatform`; wire into VM startup
- [ ] Route `__println` / `__readline` through `platform.stdout_write` / `stdin_readline`
- [ ] Add `[Native]` attribute parsing in compiler (TypeChecker + Codegen)
- [ ] Compile `z42.core` (Convert, Assert) as first stdlib module
- [ ] Compile `z42.io` (Console, File, Path, Environment)
- [ ] Compile `z42.math` (Math)
- [ ] Add stdlib search-path resolution in VM startup
- [ ] Update `BuiltinTable.cs` pseudo-class fallback logic
- [ ] Golden tests: each stdlib module has at least one test in `z42.Tests`
