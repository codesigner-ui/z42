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

### Per-Package Extern Budget（每包 extern 预算）

> **规则**：stdlib 包**只有 `z42.core` 或直接依赖 native 第三方库**的才允许使用
> `extern` + `[Native]`。其他包**必须**纯脚本实现。

**允许使用 extern 的场景：**

| 包 | 理由 |
|----|------|
| `z42.core` | 语言基石：primitive 方法、核心字符串操作、Object 协议必须由 VM 提供 |
| `z42.io` | 依赖 native OS API（文件系统、标准流、时间、环境变量）|
| `z42.math` | 依赖 libm（Sqrt / Log / 三角函数等需要 native 精度）|
| 未来 `z42.compression` / `z42.crypto` 等 | 封装第三方 native 库（zlib / OpenSSL 等）|

**禁止使用 extern 的场景：**

- 纯逻辑操作（集合、字符串构建、算法）— 用脚本实现
- Rust 内部优化（例如 `StringBuilder` 用 Rust 可变 String 加速）—
  **不是合法理由**；这属于"内部优化"而非"native 库依赖"
- "这样写更快" 作为单独理由 — 必须有实测性能证据且符合上面的 3 层升级规则

**判定原则：**
- "依赖 native 库" = 依赖**外部的** C / Rust 库（系统或第三方），不是 VM 内部实现
- VM 内部 Rust 函数不算 native 库 — 如果需求能用 z42 源码表达就应该这样写

**当前违规：** 无（2026-04-26 起；`StringBuilder` 已纯脚本化、`__list_*` / `__dict_*` 已删除）。

### Layer 3 Package Allowed-Extern Summary

```
z42.core         ✅ extern 允许（语言基石）
z42.io           ✅ extern 允许（OS 依赖）
z42.math         ✅ extern 允许（libm 依赖）
z42.text         ✅ 已纯脚本（StringBuilder 2026-04-26 迁移完成）
z42.collections  ✅ 已纯脚本（L3-G4h 完成）
z42.numerics     ✅ 未来必须纯脚本（无 native 依赖）
```

---

## Layer 1 — VM Intrinsics

Functions that cannot be implemented in z42 because they require direct Rust/OS access.
Exposed to script code via the `Builtin` IR instruction (`__name` convention).

**File layout（实际目录 `src/runtime/src/corelib/`）：**

```
src/runtime/src/corelib/
├── mod.rs        ← dispatch entry: exec_builtin(name, args)
├── string.rs     ← __str_length / __str_char_at / __str_from_chars / …
├── char.rs       ← __char_is_whitespace / __char_to_lower / __char_to_upper
├── io.rs         ← __println, __print, __readline, __concat, __len, __contains
├── math.rs       ← __math_abs / __math_max / __math_min / __math_pow / __math_sqrt / …
├── convert.rs    ← __int_parse / __long_parse / __double_parse / __to_str
├── fs.rs         ← __file_* / __path_* / __env_* / __process_exit / __time_now_ms
└── object.rs     ← __obj_get_type / __obj_ref_eq / __obj_hash_code / __assert_*
```

**Naming convention:** all intrinsic names start with `__`, are lowercase with underscores.

**Current intrinsics (SoT)：** 由 [src/libraries/README.md "Extern 现状审计表"](../../src/libraries/README.md)
维护，每次 stdlib 改动起手必看。本文不再重复列出（避免 bit-rot）。

**Adding a new intrinsic requires changes in three places (enforced rule):**
1. `corelib/<module>.rs` — implementation
2. `corelib/mod.rs` — dispatch entry
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
| `z42.collections` | `Std.Collections` | Loaded when `using Std.Collections;` is present（**仅覆盖 Queue/Stack 等次级集合**；`List<T>` / `Dictionary<K,V>` 虽属同 namespace，但物理驻留 `z42.core`，随 prelude 一起加载）| C# `System.Collections.Generic`（非基础部分） |
| `z42.text` | `Std.Text` | Loaded when `using Std.Text;` is present | C# `System.Text` |
| `z42.math` | `Std.Math` | Loaded when `using Std.Math;` is present | C# `System.Math` |

**`z42.core` auto-load semantics:**
- Loaded before the first instruction of any user module is executed.
- All names exported by `Std` (`z42.core`) are available in every file without qualification.
- It is a compile-time error to redeclare a top-level name that shadows a `Std` export without an explicit alias (L3).
- User projects **must not** declare `z42.core` as an explicit dependency in their `.z42.toml`; it is injected automatically by the compiler and VM.

### strict-using-resolution (2026-04-28)

The compiler enforces strict package-based using resolution
（[namespace-using.md](namespace-using.md#strict-using-resolution-2026-04-28)）：

- **Prelude whitelist**：硬编码 `Z42.Core.PreludePackages.Names = { "z42.core" }`。
  仅 z42.core 的 namespace 默认可见；扩展需 spec proposal。
- **using 必须激活包**：使用 `Console`、`Math`、`Queue`、`StringBuilder`、`Random`
  等非 prelude 类型，必须写对应 using（`Std.IO` / `Std.Math` / `Std.Collections`
  / `Std.Text` / `Std.Math`）。
- **多包同 namespace 允许**：例 `Std.Collections` 由 z42.core (List/Dictionary)
  与 z42.collections (Queue/Stack) 共同提供 — 合法，前提是类型名不冲突。
  同 (namespace, class-name) 多包提供 → E0601。
- **保留前缀**：第三方包（不以 `z42.` 开头）声明 `Std` / `Std.*` namespace →
  W0603 软警告，不阻断（避免阻止外部包临时调试）。

**诊断码**（[error-codes.md](error-codes.md)）：
- E0601 NamespaceCollision — 跨包同 (ns, name) 冲突
- E0602 UnresolvedUsing — using 指向未加载的 namespace
- W0603 ReservedNamespace — 非 stdlib 包占用 Std.* 前缀

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
├── Object.z42          # ToString, Equals, GetHashCode stubs
├── String.z42 + primitive structs (Int/Long/Double/Float/Bool/Char)
├── Type.z42            # runtime type object (typeof result)
├── Convert.z42         # Convert.ToInt32, Convert.ToDouble, Convert.ToString
├── Assert.z42          # Assert.Equal, Assert.True, Assert.Null, …
├── IEquatable.z42      # interface IEquatable<T>
├── IComparable.z42     # interface IComparable<T>
├── IDisposable.z42     # interface IDisposable
├── INumber.z42         # interface INumber<T> (op_Add / op_Subtract / …)
└── Collections/        # 基础泛型集合三件套（namespace Std.Collections）
    ├── List.z42        # List<T>       — 纯脚本动态数组
    ├── Dictionary.z42  # Dictionary<K,V> — 纯脚本开放寻址哈希表
    └── HashSet.z42     # （未来实现）
```

**2026-04-25 reorganize-stdlib-packages W1 调整**：
最基础三件套 `List<T>` / `Dictionary<K,V>` / `HashSet<T>` 从 `z42.collections`
包迁至 `z42.core/src/Collections/`，对齐 C# BCL（`System.Collections.Generic` 位于
`System.Private.CoreLib` assembly）。`sources.include` 默认递归通配
`src/**/*.z42`，子目录自动拾取。namespace 仍为 `Std.Collections`（物理包位置与
namespace 分层解耦；用户代码需 `using Std.Collections;`）。

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

### `z42.collections` — 次级集合（非基础三件套）

> **2026-04-25 重组**：基础三件套 `List<T>` / `Dictionary<K,V>` / `HashSet<T>`
> 已上提至 `z42.core/src/Collections/`，与核心类型共享隐式 prelude 包。本包
> 仅保留次级集合（需要显式 `using Std.Collections;` 触发加载）。

```
src/libraries/z42.collections/src/
├── Queue.z42         # Queue<T>  — FIFO（L3 源码实现）
└── Stack.z42         # Stack<T>  — LIFO（L3 源码实现）
```

**未来扩展（L2/L3 按需补齐）**：`LinkedList<T>` / `SortedDictionary<K,V>`
/ `PriorityQueue<T>`。

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
