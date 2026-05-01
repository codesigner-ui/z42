# Language Customization Framework

> This document describes z42's customization architecture: how the compiler can be configured to support different language dialects, enable/disable features, and adapt to different execution environments (embedded, restricted, educational, etc.).

---

## Design Principle

z42 compiler follows a core principle:

> **Code provides mechanism; configuration provides policy.**

This means:

- The compiler **core** handles "how to parse/type-check/codegen"
- **What features are allowed**, **which operators work**, and **how strict checking is** are determined by **data-driven configuration**
- Adding a new operator, disabling a statement, or requiring stricter rules needs **only configuration changes**, not code changes

This makes z42 both a **complete systems programming language** and a **highly customizable embedded scripting engine**.

---

## Configuration Architecture

### Layer 1: LanguageFeatures — Feature Toggles

`LanguageFeatures` (in the compiler) is the top-level customization point. Each feature is identified by a `snake_case` string, with a boolean value (`true`/`false`).

**Built-in Presets:**

| Preset | Purpose |
|--------|---------|
| `Minimal` | Smallest viable subset (arithmetic, I/O, control flow) |
| `Phase1` | Full L1 feature set (default for z42) |

**Known Features:**

| Feature Name | Controls |
|--------------|----------|
| `control_flow` | `if` / `while` / `do` / `for` / `foreach` / `break` / `continue` |
| `exceptions` | `try` / `catch` / `finally` / `throw` |
| `pattern_match` | `switch` expressions and statements |
| `oop` | `class` / `interface` / `struct` / `record` / `new` |
| `arrays` | Array creation and indexing |
| `bitwise` | `&`, `\|`, `^`, `~`, `<<`, `>>` and compound assignments |
| `null_coalesce` | `??` operator |
| `ternary` | `? :` ternary operator |
| `cast` | `(Type)expr` explicit cast |
| `lambda` | Lambda expressions `=>` (L2: no-capture / L3: full closure — see [`closure.md`](closure.md)) |
| `async` | `async` / `await` (L3) |
| `nullable` | `T?` nullable types |
| `interpolated_str` | `$"..."` string interpolation |
| `tuples` | Tuple types and literals (L3) |
| `delegates` | Function types `(T) -> R` (L2 — see [`closure.md`](closure.md)) |
| `reflection` | `typeof` / `nameof` |
| `threading` | `lock` / multi-threading (L3) |
| `using_stmt` | `using` statement (resource management) |

**Example:**

```csharp
// Game script engine: allow arithmetic, control flow, arrays, but not OOP
var scriptFeatures = LanguageFeatures.Minimal.WithOverrides(new()
{
    ["control_flow"] = true,
    ["arrays"] = true,
    ["interpolated_str"] = true,
    // OOP, exceptions, threading remain false
});

var compiler = new Compiler(scriptFeatures);
```

### Layer 2: ParseTable — Operator & Statement Registry

`ParseTable` is the **single source of truth** for operator precedence and statement parsing rules.

#### Expression Rules

Each operator is defined by:

```csharp
[TokenKind.Plus] = new ParseRule(
    leftBp: 70,              // Precedence when used as infix
    nud: Nuds.BinaryUnary,   // Prefix handler
    led: Leds.BinaryLeft,    // Infix handler
    feature: null            // null = always enabled
);
```

**Precedence Levels** (spaced by 10 for easy insertion):

```
10  Assignment       (right-associative)
20  Ternary / ??
30  Logical OR   ||
40  Logical AND  &&
44  Bitwise OR   |       [feat:bitwise]
46  Bitwise XOR  ^       [feat:bitwise]
48  Bitwise AND  &       [feat:bitwise]
50  Equality     == !=
60  Comparison   < <= > >= is as
65  Shift        << >>    [feat:bitwise]
70  Addition     + -
80  Multiplication * / %
90  Postfix      () . [] ++ --
```

#### Statement Rules

Each keyword is defined by:

```csharp
[TokenKind.For] = new StmtRule(
    handler: Stmts.For_,     // Parsing function
    feature: "control_flow"  // Optional feature gate
);
```

**Adding a new statement is a two-step process:**

1. Add entry to `StmtRules` with handler and optional feature name
2. Implement the handler function in `Stmts.cs`

### Layer 3: Handler Functions

**Nud (Null Denotation)** — Prefix/atomic expressions:

```csharp
// In Nuds.cs
public static Expr BinaryUnary(ParserContext ctx, Token tk) {
    // Parse unary minus: -expr
}
```

**Led (Left Denotation)** — Infix/postfix expressions:

```csharp
// In Leds.cs
public static Expr BinaryLeft(ParserContext ctx, Expr left, Token tk) {
    // Parse binary addition: left + right
}
```

**Stmts** — Statement parsers:

```csharp
// In Stmts.cs
public static Stmt For_(ParserContext ctx, Token tk) {
    // Parse for loop
}
```

---

## Customization Scenarios

### Scenario 1: Embedded Game Script Engine

Allow only arithmetic, control flow, and basic I/O. Forbid OOP, exceptions, and threading:

```csharp
var gameScriptFeatures = LanguageFeatures.Minimal.WithOverrides(new()
{
    ["control_flow"] = true,
    ["arrays"] = true,
    ["interpolated_str"] = true,
    ["reflection"] = false,  // No typeof/nameof
    ["exceptions"] = false,
    ["threading"] = false,
    ["oop"] = false,
});

var gameCompiler = new Compiler(gameScriptFeatures);
var bytecode = gameCompiler.Compile(scriptSource);
gameVM.LoadModule(bytecode);
```

**Result:** Game scripts can use `if`, `for`, `Console.WriteLine`, arrays — but not classes, exceptions, or threads.

### Scenario 2: Educational Environment (Gradual Feature Introduction)

Week 1: Only basic types and output.
Week 2: Add control flow.
Week 3: Add OOP.

```csharp
// Week 1
var week1 = LanguageFeatures.Minimal
    .WithOverrides(new() {
        ["interpolated_str"] = true,
    });

// Week 2
var week2 = week1.WithOverrides(new() {
    ["control_flow"] = true,
});

// Week 3
var week3 = week2.WithOverrides(new() {
    ["oop"] = true,
});

foreach (week in [week1, week2, week3]) {
    var compiler = new Compiler(week);
    RunStudentCode(compiler, studentFile);
}
```

### Scenario 3: Restricted Production Environment

High-security server where dynamic dispatch is forbidden (prefer static types), exceptions are limited, and reflection is disabled:

```toml
# production/z42.toml

[language.restrictions]
pattern_match = false          # No switch fallthrough
reflection = false             # No typeof/nameof
threading = false              # Single-threaded
nullable = false               # No T?; use Option<T> only (L3)
```

```z42
// In safe production code:
public Result<int, Error> ProcessRequest(Request req) {
    // ✓ Return Result; structured error handling
    // ✓ No reflection
    // ✓ No nullable refs
}
```

### Scenario 4: Experimental Operator (Power Operator `**`)

Add a new operator without affecting existing code:

**Step 1: Add to ParseTable**

```csharp
// ParseTable.cs
[TokenKind.StarStar] = new(
    leftBp: 75,
    nud: null,
    led: Leds.BinaryLeft("**"),
    feature: "pow_operator"  // New feature gate
);
```

**Step 2: Register in LanguageFeatures**

```csharp
// LanguageFeatures.cs
public static readonly LanguageFeatures Minimal = new()
{
    // ... existing features ...
    ["pow_operator"] = false,  // Disabled by default in Minimal
};

public static readonly LanguageFeatures Phase1 = new()
{
    // ... existing features ...
    ["pow_operator"] = true,   // Enabled in Phase1
};
```

**Step 3: Use it**

```z42
int power = 2 ** 8;  // 256 — works if "pow_operator" is enabled
```

---

## Implementation Checklist

When adding a new feature:

1. **Define in LanguageFeatures:**
   ```csharp
   ["my_feature"] = false;  // Minimal
   ["my_feature"] = true;   // Phase1 (if stable)
   ```

2. **Register in ParseTable or StmtRules:**
   ```csharp
   [TokenKind.MyKeyword] = new(..., feature: "my_feature");
   ```

3. **Implement handler:**
   ```csharp
   // In Nuds.cs, Leds.cs, or Stmts.cs
   public static ... MyHandler(ParserContext ctx, ...) { ... }
   ```

4. **Add test:**
   ```csharp
   [Fact]
   public void TestMyFeatureWhenEnabled() {
       var features = LanguageFeatures.Minimal.WithOverrides(new() {
           ["my_feature"] = true
       });
       var result = new Parser(features).Parse(code);
       // Assert parsing succeeds
   }
   
   [Fact]
   public void TestMyFeatureDisabledByDefault() {
       var features = LanguageFeatures.Minimal;
       var parser = new Parser(features);
       // Assert parsing fails
   }
   ```

5. **Validate with GrammarSyncTests:**
   ```csharp
   // Automatic test ensures all feature names in ParseTable
   // exist in LanguageFeatures.KnownFeatureNames
   ```

---

## Design Constraints

- **Feature names must be snake_case** for consistency and IDE completion.
- **New features must be declared** in both `Minimal` and `Phase1` presets.
- **GrammarSyncTests** validate that all feature names in `ParseTable` are declared in `KnownFeatureNames` (catches typos).
- **No feature can bypass type safety** — feature gates only enable/disable syntax; type checker is always strict.

---

## Comparison with Other Languages

| Approach | Runtime Feature Gates | Config-Driven | Suitable for Embedding |
|----------|:---:|:---:|:---:|
| **z42 ParseTable** | ✅ | ✅ | ✅ |
| pest (.pest files) | ❌ (compile-time) | ✅ | ❌ |
| nom (combinator) | ❌ | ❌ | ❌ |
| Roslyn Scripting | ❌ | ❌ | ✅ (but not trimmed) |
| Python (sys.modules) | ✅ | ✅ | ✅ (but different model) |

z42's core advantage is **runtime feature gating** with **zero performance overhead** (feature checks happen at parse time, not runtime).

---

## Future Extensions (L3+)

- **Attribute-level gates:** `[Strict]` on individual functions for stricter rules.
- **Capability-based security:** Fine-grained permissions ("can call native", "can use IO").
- **ABI versioning:** Lock down struct layouts to prevent binary breaks.
- **Language profiles:** Pre-configured sets (e.g., "safe", "performance", "embedded").

---

## Related Documents

- [philosophy.md](philosophy.md) — Customization-first design principle
- [language-overview.md](language-overview.md) — Feature syntax examples
- [features.md](../features.md) — Feature definitions and phase assignments
