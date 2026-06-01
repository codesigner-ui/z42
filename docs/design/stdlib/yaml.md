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
    public static YamlValue   Parse(string text);                  // single doc, strict
    public static YamlValue[] ParseAll(string text);               // multi-doc (`---`)
    public static string      Stringify(YamlValue root);
}

namespace Std;
public class YamlException : Exception { /* */ }
```

## Stream overloads（2026-05-24 add-stream-overloads-to-format-parsers）

| Method | Signature |
|--------|-----------|
| `YamlValue.ParseStream` | `(Std.IO.Stream) → YamlValue` — UTF-8 drain + decode (single-doc, strict); src not closed |
| `YamlValue.ParseAllStream` | `(Std.IO.Stream) → YamlValue[]` — UTF-8 drain + multi-doc decode (`---` separator); src not closed (add-yaml-multi-doc, 2026-05-25) |
| `YamlValue.WriteTo` | `(Std.IO.Stream, YamlValue) → void` — canonical YAML, UTF-8; dest not closed |

See [`json.md` Stream overloads](json.md#stream-overloads2026-05-24-add-stream-overloads-to-format-parsers)
for the rationale on the `ParseStream` naming.

## Supported syntax (v0)

| Feature | Example |
|---------|---------|
| Scalars: null | `~` / `null` / empty |
| Scalars: bool | `true` / `false` (YAML 1.2 — no `yes`/`on`/`off` Norway-problem) |
| Scalars: int  | `42` / `-7` (decimal, signed) / `0xFF` / `0o755` (hex, octal — YAML 1.2; lowercase prefix only) |
| Scalars: float | `3.14` / `1.5e10` (decimal, exponent OK) |
| Scalars: plain string | `hello world` |
| Scalars: double-quoted | `"a\nb"` with `\n \t \r \" \\ \/ \0 \uXXXX` escapes |
| Scalars: single-quoted | `'don''t'` (no escapes except `''` → `'`) |
| Scalars: block literal | `key: \|\n  Line1\n  Line2\n` → `"Line1\nLine2\n"` (newlines preserved; chomping `-` strip / `+` keep / default clip) |
| Scalars: block folded | `key: >\n  A\n  B\n` → `"A B\n"` (consecutive non-blank lines fold to single space; blank line → `\n`) |
| Anchors / aliases | `defaults: &defaults\n  k: v\nprod: *defaults` — per-doc scope; aliases resolve to a `DeepClone` of the anchored value (no shared mutation) |
| Merge keys | `prod:\n  <<: *defaults\n  override: true` — YAML 1.1 `tag:yaml.org,2002:merge`; value must be a mapping or sequence of mappings; explicit keys override merged regardless of position; quoted `"<<"` stays literal (escape hatch); `Stringify` emits the expanded form (no `<<:` in output) |
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

### ~~`yaml-future-anchors`~~ — **✅ 已落地 2026-05-25 (add-yaml-anchors-aliases)**

Shipped: YAML 1.2 anchor declarations (`&name`) + alias references
(`*name`). Per-document scope (multi-doc inputs reset the anchor table
between docs; same anchor name reusable across docs). Resolution via
`YamlValue.DeepClone()` — aliases are independent copies so mutating
one alias never leaks to the source or sibling aliases (uniform clone
semantics regardless of scalar/sequence/mapping kind).

API surface unchanged — anchor/alias detection happens inside the
parser, transparently to callers. New private `_TryParseAnchorOrAlias
(parentIndent) → YamlValue?` is wired into mapping-value + sequence-item
dispatch points (precedes block-scalar + plain-scalar fallbacks).
Anchor name chars: alphanumeric + `_` `-` `.` (covers all common
use cases; YAML 1.2 §6.9 is more permissive but rare in practice).

17 tests cover inline scalar anchor/alias, string anchor, block-mapping
anchor (kubectl/helm `defaults: &defaults` pattern), block-sequence
anchor, anchor-in-sequence-item, anchor+flow sequence/mapping, multiple
independent anchors, multiple aliases-to-same anchor, anchor name with
`_` / `-`, undefined-alias error, forward-reference error, per-doc
scope reset (cross-doc alias throws), same anchor name reused across
docs OK, nested mapping anchor + deep clone, alias snapshot independence
under post-parse mutation.

### ~~`yaml-future-tags`~~ — **✅ 已落地 2026-05-25 (add-yaml-tags)**

Shipped: YAML 1.2 universal type tags (`!!str`, `!!int`, `!!float`,
`!!bool`, `!!null`) for explicit scalar type coercion. Most common
use case is `!!str 42` to force numeric-looking values to stay as
strings (Kubernetes ConfigMap data fields are the canonical example).

| 用法 | 解释 |
|---|---|
| `key: !!str 42` | force `"42"` (string), not `42` (int) |
| `key: !!int "42"` | force `42` (int), parse the quoted string |
| `key: !!float "1.5"` | force float parse |
| `key: !!bool true` / `!!bool FALSE` | strict YAML 1.2 bool (yes/on/off rejected) |
| `key: !!null` / `!!null ~` / `!!null null` | explicit null |
| `key: !myTag value` | local tag — silently ignored, fall back to scalar inference (z42 v0 has no user tag registry) |
| `key: !!binary hex` | unknown `!!` tag — same fall-through |

Implementation: new `_TryParseTag(parentIndent) → YamlValue?` in
YamlParser, wired into mapping-value + sequence-item dispatch ahead
of anchor/alias and block-scalar paths. Tag handle parsed greedily
(`!`, `!!`, or `!`/`!!` + identifier chars). Following inline scalar
captured verbatim, then `_ApplyTagCoercion(tag, raw)` produces the
typed YamlValue or throws YamlException on type mismatch (e.g.
`!!int notanumber`).

20 tests cover: !!str on int/bool/null-looking and quoted values;
!!int with quoted digits, plain int, malformed value (throws);
!!float quoted digit, malformed (throws); !!bool quoted/uppercase,
non-1.2-bool like `yes` (throws); !!null with empty/~/null and
non-null (throws); tags in sequence items (`- !!str 42`);
local/unknown tags fall through to scalar inference; Kubernetes
ConfigMap pattern (`port: !!str 8080`, `enabled: !!str true`).

Out of scope (no follow-up planned unless user demand surfaces):
- `!!seq` / `!!map` collection tags (usually inferred from block
  structure; explicit tag adds no info)
- Verbose URI form `!<tag:yaml.org,2002:str>` (rare in practice;
  `!!str` is the standard short form)
- User-extensible tag registry (no z42 use case yet)
- Tag combined with anchor in the same value position (`&a !!str 42`
  or `!!str &a 42`) — rare; current dispatch is tag-first then
  anchor on the resulting value

### ~~`yaml-future-multiline-strings`~~ — **✅ 已落地 2026-05-25 (add-yaml-multiline-strings)**

Shipped: YAML 1.2 block scalars `|` (literal) and `>` (folded), with full
chomping indicator support (`-` strip / `+` keep / default clip) and
optional indent indicator (digit 1-9). Auto-detect content indent from
first non-blank line; deeper indentation preserved relative to that base.
Implemented as `_TryParseBlockScalar(parentIndent) → YamlValue` wired into
mapping-value + sequence-item dispatch points; returns null when current
position is not a `|` / `>` indicator (caller falls back to plain-scalar
parse).

18 tests cover: literal 1/2 line + blank-line preservation + multi-blank
preservation; strip / clip / keep chomping for both styles; folded
space-folding (3 consecutive non-blank lines → "A B C"); folded
blank-as-newline; literal + folded in sequence items; sibling mapping
key after block scalar; deeper-indented content preserved; empty block
scalar (`key: |` with no content); EOF without trailing newline in source.

### ~~`yaml-future-multi-doc`~~ — **✅ 已落地 2026-05-25 (add-yaml-multi-doc)**

Shipped: `YamlValue.ParseAll(string) → YamlValue[]` and
`ParseAllStream(Stream) → YamlValue[]` for kubectl-style multi-document
YAML (`---` separator). Internal refactor extracts `_ParseOneDocBody()`
from `ParseDocument`; adds `_IsDocBoundary()` helper checked at
`_ParseBlockValue` / `_ParseBlockMapping` / `_ParseBlockSequence` yield
points so root-level `---` / `...` markers don't get mis-parsed as
plain scalars / sequence dashes. `Parse(string)` keeps strict single-doc
behaviour but now hints "use ParseAllDocuments for multi-doc YAML" in
the error message.

17 tests cover: 2-doc / 3-doc separator / leading `---` / trailing `...` /
`... + ---` between docs / empty + whitespace-only input → empty array /
bare `---` → null doc / two `---` → two null docs / single-doc via
ParseAll / mixed-types doc stack (scalar + sequence + mapping) /
nested mapping per doc / sequence-then-mapping / kubectl manifest
stack / single-doc `Parse()` still rejects multi-doc with hint /
single-doc `Parse()` accepts trailing `...` / comments between docs.

### ~~`yaml-future-merge-keys`~~ — **✅ 已落地 2026-06-01 (add-yaml-merge-keys)**

Shipped: YAML 1.1 merge-key extension `<<: *anchor`
(`tag:yaml.org,2002:merge`). Block + flow mapping support;
quoted `"<<"` / `'<<'` stays literal as the documented escape hatch.
Merge value must resolve to a mapping or a sequence of mappings;
scalars, nulls, mixed sequences throw `YamlException` with "merge"
in the message. Precedence: explicit keys override merged values
regardless of source position; within `<<: [*a, *b]` earlier source
wins (per spec text). Stringify emits the expanded mapping (no `<<:`
in output) — round-trip preserves data, not the merge syntax.

Implementation: new `_ApplyMerge(dst, mergeVal)` + `_MergeOneSource` +
linear `_mergedKeys` / `_mergedCount` field tracking which keys came
from a merge so an explicit assignment can silently overwrite.
`_ParseBlockMapping` and `_ParseFlowMapping` save/restore that table
across recursion. `_lastKeyQuoted` flag (set by `_ParseMappingKey` /
`_ParseFlowKey`) distinguishes plain `<<` (merge) from quoted `"<<"`
(literal). `_ParseFlowValue` gained a one-liner `*alias` branch —
needed because the block-context alias parser consumes the rest of
the line, which is wrong for flow-bounded values.

16 tests in `tests/parse_merge_keys.z42` cover: basic single-anchor
merge, explicit override after `<<:`, explicit before `<<:`,
sequence-of-anchors earlier-wins, flow mapping merge,
double-quoted + single-quoted `<<` escape hatch, direct flow-mapping
as merge value, alias-to-scalar / sequence-with-non-mapping / null
error paths, Docker Compose `x-common` pattern (`web` / `worker`
sharing `restart` + `logging`), DeepClone independence (mutating
left.count doesn't leak to right.count), Stringify drops `<<:`,
per-doc scope via `ParseAll`, duplicate explicit-after-merge still
errors.

Out of scope (no follow-up planned unless demand surfaces):
- **Re-emit `<<:` in `Stringify`** — would require per-key provenance
  tracking. Configs that author with `<<:` are typically read by
  tools, not round-tripped.
- **Deep merge** — merged mappings are inserted shallowly; an
  explicit key with a mapping value replaces the merged mapping
  wholesale (matches PyYAML default).
- **`&anchor` in flow contexts** — separate pre-existing gap
  unrelated to merge; add when needed.

### `yaml-future-complex-keys`

- **来源**：add-z42-yaml v0 scope
- **触发原因**：YAML 1.2 allows sequences / mappings as keys via the
  `? key` syntax. Rare in practice — v0 supports string keys only.

### ~~`yaml-future-timestamps`~~ — **✅ 已落地 2026-05-27 (add-yaml-timestamps)**

Shipped: kind 7 (Timestamp) joins YamlValue's discriminator. Factories
`OfTimestamp(DateTime)` (canonical UTC + ms output) and
`OfTimestampString(iso)` (round-trip-preserves the original lexeme so
non-UTC offset / sub-ms precision survive Stringify). Parser
auto-recognises ISO 8601 prefix shapes (`YYYY-MM-DD` + optional
`T`/`t`/space + time + Z/±HH:MM); parse failure falls through to
numeric/string so "almost-date" strings degrade gracefully. Depends on
`DateTime.ParseIso8601` (also shipped same day —
`add-datetime-iso8601-parse`). 12 tests cover UTC / offset / date-only
/ millis / raw-lexeme round-trip / canonical UTC output / DeepClone /
fallback / sequence-of-timestamps.

### ~~`yaml-future-numeric-bases`~~ — **✅ 已落地 2026-05-25 (add-yaml-numeric-bases)**

Shipped: YAML 1.2 hex (`0xFF`) and octal (`0o755`) integer literals.
Lowercase prefix only (matches spec §10.2 schema; `0X` / `0O` stay as
strings). No sign permitted on non-decimal literals. Decimal literals
unchanged. 19 tests cover hex upper/lower/mixed case, octal file-mode
patterns (`0o755` = 493, `0o644` = 420), hex in mapping value / sequence,
rejection paths (uppercase prefix → string, invalid digit → string,
bare `0x` / `0o` → string), decimal-with-leading-zero stays decimal,
long-max hex (`0x7FFFFFFFFFFFFFFF`).
