# Proposal: add z42.toml

## Why

The z42 build / test scripts currently live in bash and shell out to
`python3 -c 'import tomllib; ...'` to parse `versions.toml`. To migrate
those scripts to z42 (the long-term self-hosted build-driver plan in
[stdlib roadmap](../../../design/stdlib/roadmap.md) → "z42 build-driver
prerequisites"), z42 needs a native TOML reader. Currently no z42 stdlib
package can parse `[project]` / `[dependencies]` / `[[exe]]` sections,
forcing the python3 indirection on every dev machine and CI.

Beyond build scripts: z42 itself uses TOML for manifests (`*.z42.toml`)
and any user app reading config files will reach for TOML. Having a
first-class `Std.Toml.Parse(text)` makes z42 self-sufficient for the
common config-file use case.

## What Changes

1. New stdlib package `z42.toml` (depends only on `z42.core`).
2. Public API: `Std.Toml.{Parse, Stringify}` + `Std.TomlValue` discriminated
   value type + `Std.TomlException`.
3. TOML 1.0 subset (sufficient for z42 manifests and `versions.toml`):
   - Top-level + nested-section key/value (`name = "x"`, `[a.b]`)
   - Array of tables (`[[exe]]`)
   - Inline tables (`{ k = v, ... }`)
   - Strings (basic `"…"` + literal `'…'`)
   - Integers (i64, decimal only — no underscores in v0)
   - Floats (f64, including `1.5e10` exponent form)
   - Booleans (`true` / `false`)
   - Arrays (homogeneous + heterogeneous)
   - Comments (`# …`, end-of-line and standalone)
4. Workspace + build-stdlib + index.json registration.
5. README + design doc (`docs/design/stdlib/toml.md`).
6. No new VM builtins (pure script atop `String.CharAt` / `String.Substring`
   / `List<T>` / `Dictionary<K,V>`).

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.toml/z42.toml.z42.toml` | NEW | Manifest (`name = "z42.toml"`, dep on z42.core) |
| `src/libraries/z42.toml/src/Toml.z42` | NEW | Public facade `Parse` / `Stringify` |
| `src/libraries/z42.toml/src/TomlValue.z42` | NEW | Discriminated value type + factory helpers + `Is*` / `As*` |
| `src/libraries/z42.toml/src/TomlParser.z42` | NEW | Internal recursive-descent parser + tokenizer |
| `src/libraries/z42.toml/src/TomlWriter.z42` | NEW | Internal stringifier |
| `src/libraries/z42.toml/src/TomlException.z42` | NEW | Exception subclass (Std.Exception, in z42.toml zpkg — cross-pkg fix already shipped) |
| `src/libraries/z42.toml/tests/parse_basic.z42` | NEW | Top-level k/v, sections, types |
| `src/libraries/z42.toml/tests/parse_array.z42` | NEW | Arrays, inline tables, array of tables |
| `src/libraries/z42.toml/tests/parse_strings.z42` | NEW | Basic / literal strings, escapes |
| `src/libraries/z42.toml/tests/parse_errors.z42` | NEW | Malformed input → TomlException |
| `src/libraries/z42.toml/tests/stringify.z42` | NEW | Round-trip + format checks |
| `src/libraries/z42.toml/README.md` | NEW | Package README |
| `src/libraries/z42.workspace.toml` | MODIFY | Add `"z42.toml"` to default-members |
| `scripts/build-stdlib.sh` | MODIFY | Add `z42.toml` to LIBS + index.json `Std.Toml` mapping |
| `docs/design/stdlib/toml.md` | NEW | Design doc (architecture + Deferred section) |
| `docs/design/stdlib/roadmap.md` | MODIFY | Move z42.toml from P2 (was unranked) to 已落地 |
| `docs/design/stdlib/organization.md` | MODIFY | Add z42.toml to current state table |
| `src/libraries/README.md` | MODIFY | Add z42.toml row |

**只读引用**：
- `src/libraries/z42.encoding/` — pattern reference (similar pure-script stdlib)
- `src/libraries/z42.io/src/Stdio.z42` — discriminated-union pattern reference

## Out of Scope (Deferred to v1+)

- **TOML datetimes** (`offset-date-time`, `local-date-time`, `local-date`,
  `local-time`) — punt to v1, requires z42.time `DateTime` / `TimeSpan`
  integration design (current `DateTime` is only Unix-ms; TOML wants
  calendar fields).
- **Multiline strings** (`"""…"""` and `'''…'''`) — uncommon in configs.
- **Non-decimal integer literals** (`0x`, `0o`, `0b`) — uncommon in configs.
- **Underscore digit separators** (`1_000`) — cosmetic.
- **Stringify formatting controls** (indent style, key sort) — single
  canonical output for v0; pretty-print is future work.
- **Round-trip preservation** (comments, key order) — values round-trip,
  but layout / comments are dropped on parse. Comment-preserving AST is
  future work (Tomlyn-style).
- **Schema validation / serde-style deserialize** — needs reflection or
  lambdas (L3+).

## Open Questions

- [ ] Should `Std.Toml.Parse` reject duplicate keys per TOML spec, or
      last-wins? **Default**: reject (matches TOML 1.0 spec section
      "Keys"). Document explicitly.
- [ ] Float NaN / Inf encoding: TOML 1.0 includes `nan` / `inf` / `+inf`
      / `-inf` literals. **Default**: support on parse, emit
      `nan` / `inf` / `-inf` on stringify (matches Rust `toml` crate).
- [ ] Array-of-tables interleaved with other sections — error or accept?
      TOML 1.0 specifies "you cannot break an existing table" — **default**:
      reject with TomlException.
