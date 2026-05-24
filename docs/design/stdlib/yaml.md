# z42.yaml

YAML 1.2 subset reader / writer. Pure-script stdlib mirroring
`z42.toml` and `z42.json` — same `XxxValue` discriminated-union shape
+ `Parse` / `Stringify` static entry points.

> Spec: [`docs/spec/archive/2026-05-24-add-z42-yaml/`](../../spec/archive/2026-05-24-add-z42-yaml/)
> shipped 2026-05-24.

## v0 API

```z42
namespace Std.Yaml;

public class YamlValue {
    // Factories
    public static YamlValue OfNull();
    public static YamlValue OfBool(bool b);
    public static YamlValue OfInt(long n);
    public static YamlValue OfFloat(double d);
    public static YamlValue OfString(string s);
    public static YamlValue OfSequence();
    public static YamlValue OfMapping();

    // Predicates
    public bool IsNull();
    public bool IsBool();
    public bool IsInt();
    public bool IsFloat();
    public bool IsString();
    public bool IsSequence();
    public bool IsMapping();
    public string KindName();

    // Accessors (throw YamlException on wrong kind)
    public bool   AsBool();
    public long   AsInt();
    public double AsFloat();
    public string AsString();

    // Sequence ops
    public int       Length();
    public YamlValue At(int i);
    public void      Add(YamlValue v);

    // Mapping ops (string keys only in v0)
    public bool      ContainsKey(string key);
    public YamlValue Get(string key);
    public void      Set(string key, YamlValue v);
    public string[]  Keys();
    public int       Count();

    // Entry points
    public static YamlValue Parse(string text);
    public static string    Stringify(YamlValue root);
}

namespace Std;
public class YamlException : Exception { /* */ }
```

## Stream overloads（2026-05-24 add-stream-overloads-to-format-parsers）

| Method | Signature |
|--------|-----------|
| `YamlValue.ParseStream` | `(Std.IO.Stream) → YamlValue` — UTF-8 drain + decode; src not closed |
| `YamlValue.WriteTo` | `(Std.IO.Stream, YamlValue) → void` — canonical YAML, UTF-8; dest not closed |

See [`json.md` Stream overloads](json.md#stream-overloads2026-05-24-add-stream-overloads-to-format-parsers)
for the rationale on the `ParseStream` naming.

> Tests for these overloads land alongside the
> `fix-yaml-parse-regression` spec (currently `YamlValue.Parse(string)`
> is broken on `main` — int / string / mapping parsing failures
> introduced between commits `249a0411` and `739112ce`).

## Supported syntax (v0)

| Feature | Example |
|---------|---------|
| Scalars: null | `~` / `null` / empty |
| Scalars: bool | `true` / `false` (YAML 1.2 — no `yes`/`on`/`off` Norway-problem) |
| Scalars: int  | `42` / `-7` (decimal, signed) |
| Scalars: float | `3.14` / `1.5e10` (decimal, exponent OK) |
| Scalars: plain string | `hello world` |
| Scalars: double-quoted | `"a\nb"` with `\n \t \r \" \\ \/ \0 \uXXXX` escapes |
| Scalars: single-quoted | `'don''t'` (no escapes except `''` → `'`) |
| Block mapping | `key: value\n` with indentation-based nesting |
| Block sequence | `- item\n` with same indentation rules |
| Sequence of inline mappings | `- name: bob\n- name: alice\n` |
| Flow mapping | `{a: 1, b: 2}` |
| Flow sequence | `[1, 2, 3]` |
| Nested flow | `[[1, 2], [3, 4]]` / `[{n: 1}, {n: 2}]` |
| Comments | `# …` (standalone + end-of-line) |
| Document marker | `---` (start) / `...` (end) — consumed but ignored (single-doc only) |
| UTF-8 source | (only) |

## Implementation notes

### Storage shape

`YamlValue` uses the discriminated-union pattern established by
`TomlValue` / `JsonValue`:

- `_kind: int` discriminator (0..=6)
- typed scalar slots (`_bool`, `_int`, `_float`, `_str`)
- parallel arrays for sequences (`_seqItems: YamlValue[] + _seqCount: int`)
  and mappings (`_mapKeys: string[] + _mapValues: YamlValue[] + _mapCount: int`)

z42 class fields don't yet take generic type parameters (so
`List<YamlValue>` is silently dropped); parallel-array storage is the
established workaround across all three config stdlibs.

### Parser strategy

Single-pass recursive descent. Each block recursion takes a required
parent-indent; child lines must indent strictly more. Lines are
processed in order; comments and blank lines are skipped at the line
boundary so they never appear mid-token.

Discrimination for an indented block: peek at the first non-WS char.
`- ` starts a block sequence; `[ ` / `{ ` starts a flow value; an
unquoted `:` followed by WS/EOL elsewhere on the line means block
mapping; otherwise it's a plain scalar.

### Stringify strategy

Block-style output, 2-space indent. Insertion order preserved (mirrors
`TomlWriter` / `JsonWriter`). Scalar quoting is conservative: any
string that would parse as a different kind (`true`, `42`, `~`, etc.)
or contains YAML structural indicators (`: # & * ! | > " ' , [ ] { }`,
control chars, leading/trailing WS, leading `- ? :`) is double-quoted
with escapes; everything else is plain.

## Deferred / Future Work

### `yaml-future-anchors`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：anchors (`&id`) / aliases (`*id`) require a parser-side
  symbol table + value de-duplication, plus careful semantics around
  cyclic references. Out-of-scope for v0; rarely used in z42's target
  config files.
- **触发条件**：use case requiring repeated value references in a
  large config (Helm / Kubernetes manifests sometimes).

### `yaml-future-tags`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：YAML tags (`!str`, `!!binary`, custom `!tag`) drive
  type-coercion overrides. Without a user-extensible tag registry the
  feature is half-baked; v0 doesn't ship the registry.
- **触发条件**：use case requiring explicit type override on a scalar
  (e.g. forcing `"42"` to stay a string without quotes).

### `yaml-future-multiline-strings`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：literal `|` and folded `>` multi-line scalars need
  scope-tracking + line-folding rules + chomping indicators. Tractable
  but ~200 lines of careful spec text → code; deferred until use case
  demands it.
- **触发条件**：use case parsing YAML with multi-line embedded scripts
  or docstrings (Helm `_helpers.tpl` etc.).

### `yaml-future-multi-doc`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：YAML allows multiple documents per file separated by
  `---`. v0 parses only the first document; trailing `---` triggers
  an error.
- **触发条件**：use case parsing `kubectl`-style stacked manifests.

### `yaml-future-complex-keys`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：YAML 1.2 allows sequences / mappings as keys via the
  `? key` syntax. Rare in practice — v0 supports string keys only.

### `yaml-future-timestamps`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：YAML can parse ISO-8601 timestamps as a dedicated
  scalar type. Without `Std.Time.DateTime` being final / well-known,
  z42.yaml can't represent these as anything richer than a string.
- **触发条件**：z42.time stabilises a DateTime type that yaml can
  emit / consume.

### `yaml-future-numeric-bases`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：YAML 1.2 supports `0o7` (octal) / `0xFF` (hex) integer
  literals. v0 supports decimal only; non-decimal literals are emitted
  as strings.
