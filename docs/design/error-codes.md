# z42 Diagnostic Codes

All error, warning, and info codes emitted by the z42 compiler.

Use `z42c --explain <code>` to view the full description and an example in the terminal.
Use `z42c --list-errors` to print a compact summary of all codes.

The canonical source of truth is [`DiagnosticCodes.cs`](../../src/compiler/z42.Compiler/Diagnostics/Diagnostic.cs) (code constants) and [`DiagnosticCatalog.cs`](../../src/compiler/z42.Compiler/Diagnostics/DiagnosticCatalog.cs) (descriptions + examples).

---

## Z01xx — Lexer

| Code   | Title                         | When it occurs |
|--------|-------------------------------|----------------|
| Z0101  | Unterminated string literal   | A `"..."` or `'...'` literal is never closed |
| Z0102  | Invalid escape sequence       | `\q`, `\p`, or other unrecognized `\` sequence |
| Z0103  | Invalid numeric literal       | `0x` with no digits, malformed float exponent, etc. |

---

## Z02xx — Parser / Syntax

| Code   | Title                    | When it occurs |
|--------|--------------------------|----------------|
| Z0201  | Unexpected token         | Parser sees a token it cannot use at this position |
| Z0202  | Expected token           | A required token (`;`, `)`, `{`, …) is missing |
| Z0203  | Unexpected end of file   | File ends before a construct is complete |
| Z0204  | Missing return type      | Function declaration has no return type (not even `void`) |
| Z0205  | Ambiguous expression     | Expression cannot be parsed unambiguously |

---

## Z03xx — Feature Gates

| Code   | Title                         | When it occurs |
|--------|-------------------------------|----------------|
| Z0301  | Language feature not enabled  | A gated syntax (e.g. lambdas) is used without enabling it |

---

## Z04xx — Type Checker

| Code   | Title                            | When it occurs |
|--------|----------------------------------|----------------|
| Z0401  | Undefined symbol                 | Variable / function / type used before declaration |
| Z0402  | Type mismatch                    | Wrong type, wrong arity, non-bool condition, break/continue outside loop, duplicate declaration |
| Z0403  | Missing return value             | Non-void function has a path with no `return` |
| Z0404  | Private member access violation  | `private` field or method accessed outside its class |
| Z0405  | Invalid modifier combination     | `abstract sealed`, modifier on enum member, etc. |
| Z0406  | Integer literal out of range     | Literal exceeds the declared explicit-size type's range (`i8 x = 200`) |

---

## Z05xx — IR Code Generator

| Code   | Title                                | When it occurs |
|--------|--------------------------------------|----------------|
| Z0501  | Unsupported syntax in code generation | A valid-syntax construct is not yet lowered to IR |

---

## Adding a new code

1. Add a `public const string Xxx = "Z0nnn";` to `DiagnosticCodes` in [Diagnostic.cs](../../src/compiler/z42.Compiler/Diagnostics/Diagnostic.cs).
2. Add an entry to `DiagnosticCatalog.All` in [DiagnosticCatalog.cs](../../src/compiler/z42.Compiler/Diagnostics/DiagnosticCatalog.cs) with title, description, and optional example.
3. Add a row to the relevant table in this file.
