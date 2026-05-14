# Proposal: add z42.json

## Why

JSON is the universal config / API / log format. With `z42.toml` shipped for
manifest parsing, the remaining gap for "z42 reads any common config" is
JSON. Use cases:

- z42 scripts consuming REST API responses or CLI tools that emit
  `--json` output (e.g. `cargo metadata --format-version=1`).
- Log structured records (line-delimited JSON).
- Future cross-package config (z42.linq query JSON literals; debugger
  protocol DAP / LSP JSON-RPC if z42 grows tooling).
- Round-trip with sister packages: read TOML, transform, write JSON.

No language change. Pure stdlib L1, mirrors `z42.toml` patterns.

## What Changes

1. New stdlib package `z42.json` (depends only on `z42.core`).
2. Public API: `Std.Json.JsonValue.{Parse, Stringify}` + `Std.JsonException`.
3. RFC 8259 full grammar support:
   - All 7 value types: string, number, true, false, null, array, object
   - String escapes (`\n \t \r \" \\ \/ \b \f \uXXXX` + surrogate pairs)
   - Numbers (integer / float — same split-storage strategy as z42.toml)
   - Nested arrays + objects with any depth
   - Whitespace tolerance
4. Two-mode stringify: compact (single line) and pretty (2-space indent).
5. Workspace + build-stdlib + index.json registration.
6. README + design doc (`docs/design/stdlib/json.md`).
7. No new VM builtins (pure script).

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.json/z42.json.z42.toml` | NEW | Manifest |
| `src/libraries/z42.json/src/JsonValue.z42` | NEW | Value type + Parse/Stringify entry points |
| `src/libraries/z42.json/src/JsonParser.z42` | NEW | Internal recursive-descent parser |
| `src/libraries/z42.json/src/JsonWriter.z42` | NEW | Internal stringifier (compact + pretty) |
| `src/libraries/z42.json/src/JsonException.z42` | NEW | Exception subclass, in `Std` namespace (like TomlException) |
| `src/libraries/z42.json/tests/parse_basic.z42` | NEW | scalars, objects, arrays |
| `src/libraries/z42.json/tests/parse_strings.z42` | NEW | escape sequences, unicode |
| `src/libraries/z42.json/tests/parse_numbers.z42` | NEW | int/float, exponents, edge cases |
| `src/libraries/z42.json/tests/parse_errors.z42` | NEW | malformed input → JsonException |
| `src/libraries/z42.json/tests/stringify.z42` | NEW | compact + pretty, round-trip |
| `src/libraries/z42.json/README.md` | NEW |  |
| `src/libraries/z42.workspace.toml` | MODIFY | Add `"z42.json"` to default-members |
| `scripts/build-stdlib.sh` | MODIFY | Add `z42.json` to LIBS + `Std.Json` mapping |
| `docs/design/stdlib/json.md` | NEW | Design doc |
| `docs/design/stdlib/roadmap.md` | MODIFY | Move z42.json from P0 to 已落地 |
| `docs/design/stdlib/organization.md` | MODIFY | Add z42.json row |
| `src/libraries/README.md` | MODIFY | Add z42.json row |

**只读引用**：
- `src/libraries/z42.toml/` — pattern reference (近一致的结构)

## Out of Scope (Deferred)

- **Schema validation** — depends on lambda / reflection (L3+)
- **Serde-style derive macros** — depends on macros / reflection (L3+)
- **JSON5** (comments, trailing commas, single quotes) — separate spec
- **JSONPath / JSONPointer** queries — separate utility package
- **Streaming reader / writer** — small docs / configs only for v0
- **Comment / key-order preservation** — same as z42.toml; future work
- **Special number handling** — JSON 不支持 NaN/Inf；z42.json 也不（与 RFC 8259 一致）

## Open Questions

- [ ] Integer overflow handling: if a JSON number > i64 range parses,
      throw or fall back to f64? **Default**: fall back to f64 (matches
      `serde_json` lossy behaviour) + document the precision caveat.
- [ ] Duplicate keys in objects: throw, last-wins, or first-wins?
      **Default**: last-wins (matches `serde_json` / `json` Python, RFC
      8259 leaves it implementation-defined).
- [ ] Empty input: parse as `null` / throw / return empty object?
      **Default**: throw (RFC 8259 requires "a single JSON value", empty
      isn't a value).
