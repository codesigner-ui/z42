# Design: YAML merge keys

## Architecture

Merge-key handling lives entirely in `YamlParser.z42`. The public
`YamlValue` shape is unchanged — by the time a parsed value reaches
the caller, the `<<` directive has been expanded and erased. This
mirrors how PyYAML / snakeyaml / js-yaml expose merges: opaque to the
data model, observable only via parse semantics.

```
_ParseBlockMapping(indent) / _ParseFlowMapping()
    │
    ├─ for each (key, val) entry parsed in source order:
    │     │
    │     ├─ if key == "<<" AND not quoted:
    │     │     _ApplyMerge(dst=m, src=val, mergedKeys)
    │     │     ── validates val is mapping OR seq-of-mappings
    │     │     ── inserts (k → DeepClone(v)) for k ∉ m
    │     │     ── records every inserted k into mergedKeys set
    │     │
    │     ├─ else if key in m:
    │     │     if key in mergedKeys:
    │     │         m.Set(key, val); mergedKeys.Remove(key)   ── explicit overrides
    │     │     else:
    │     │         throw "duplicate mapping key"
    │     │
    │     └─ else:
    │           m.Set(key, val)
    │
    └─ return m
```

`mergedKeys` is a local-to-this-mapping `string[] + count` (z42
class fields don't take generics; same parallel-array workaround as
`YamlValue` itself).

## Decisions

### Decision 1: Detect merge dispatch by `key == "<<" AND !keyWasQuoted`

**Problem:** YAML 1.2 doesn't reserve `<<`; users could in principle
have a legitimate `<<` data key.

**Options:**
- A — always treat `<<` as merge (simple, breaks the escape hatch).
- B — treat `<<` as merge only when unquoted plain; quoted `"<<"` or
  `'<<'` stays literal.
- C — require an explicit tag (`!!merge`) to opt in (matches strict
  YAML 1.2 tag schema; everybody hates writing this).

**Decision: B.** Matches PyYAML / snakeyaml / js-yaml defaults
(`safe_load` + friends all dispatch on plain `<<`). The quoted escape
hatch is well-known and survives ecosystem expectations.

**Implementation:** add a field `bool _lastKeyQuoted` set by
`_ParseMappingKey` / `_ParseFlowKey` before they return. Checked at
the dispatch site. Cheap (one branch on the first char of the key).

### Decision 2: Apply merge at parse time, not at lookup time

**Problem:** PyYAML offers `add_constructor` hooks that defer merge
resolution; we don't have hooks and the round-trip story is "the
parsed mapping is what you get".

**Options:**
- A — eager expansion at parse time (final value has the merged keys
  inlined; no `<<` token survives).
- B — lazy expansion at `Get(key)` time (preserves `<<` syntactically;
  `Stringify` could re-emit it).

**Decision: A.** B requires adding `<<` to the kind enum or a parallel
"merge sources" array on every mapping, breaking the existing storage
shape and burdening every consumer of `YamlValue`. A is uniform with
how aliases already work (alias resolution is also eager — it
DeepClones the target at parse time, not on access).

### Decision 3: Earlier merge source wins on conflicts

**Problem:** For `<<: [*a, *b]`, when both `*a` and `*b` define key
`k`, who wins?

**Options:**
- A — first (earlier) source wins.
- B — last source wins (composition style: later overrides earlier).

**Decision: A.** Matches YAML 1.1 spec text verbatim:
> "Keys in mapping nodes earlier in the sequence override keys
> specified in later mapping nodes."

This is also what PyYAML / snakeyaml do. Users habitually write
`<<: [*specific, *base]` expecting the specific override to win.

### Decision 4: Explicit keys override merged keys regardless of position

**Problem:** Should `a: 99` BEFORE `<<:` win the same way as `a: 99`
AFTER it?

**Decision: Yes, both win.** YAML 1.1 spec is explicit:
> "[explicit pairs] could include keys defined in the explicit form,
> or even keys provided via merge keys deeper in the same mapping."

Implementation handles this via the `mergedKeys` set tracking: any
key sourced from a merge can be silently overwritten by an explicit
entry; any explicit-vs-explicit collision throws.

### Decision 5: Reject non-mapping merge values eagerly

**Problem:** What should `<<: 42` or `<<: ~` do?

**Decision: Throw.** PyYAML throws; users who write `<<:` and feed
it a scalar almost certainly intended an alias that didn't resolve
to a mapping. Silent fall-through to "literal `<<` key" would mask
the bug.

Error message format: `merge key value must be a mapping or sequence
of mappings, got <kind> at (line L, col C)`.

### Decision 6: Don't re-emit `<<:` in Stringify

**Problem:** Round-trip preserves the expanded form, not the original
`<<:`-using form.

**Decision: Don't re-emit.** Re-emitting would require provenance
tracking on every mapping key (a "this came from `<<`" bit per
entry). No real demand: configs that use `<<:` are written by humans
and read by tools; tools that need to mutate-and-rewrite typically
re-author by hand. Matches PyYAML `safe_load` + `dump` default.

## Implementation Notes

### Quoted-key flag plumbing

`_ParseMappingKey` and `_ParseFlowKey` both branch on `_src[pos]`:
double-quote → `_ParseDoubleQuotedString`, single-quote →
`_ParseSingleQuotedString`, else plain. The merge dispatcher only
fires on the "else plain" branch when the resulting key string is
exactly `<<`. Implementation: set `this._lastKeyQuoted = true/false`
at each branch entry; check at the dispatch site immediately after
the key returns.

### `_ApplyMerge` helper signature

```z42
private void _ApplyMerge(
    YamlValue dst,
    YamlValue mergeVal,
    string[] mergedKeys, ref int mergedCount,
    int line, int col
)
```

- Validates `mergeVal`: if mapping, treat as one source; if sequence,
  iterate items requiring each to be a mapping; else throw.
- For each source mapping:
  - For each `k` in source.Keys() (preserves source insertion order):
    - If `dst.ContainsKey(k)` → skip.
    - Else: `dst.Set(k, source.Get(k).DeepClone())`; append `k` to
      `mergedKeys[mergedCount++]`.

`DeepClone` is needed because YAML aliases already DeepClone on
resolution (`_TryParseAnchorOrAlias` returns `target.DeepClone()`),
so the immediate `mergeVal` is already independent of the anchor
table. But each merge **copy into a new mapping** also independent
of the merge source — so each `dst` ends up with its own copy,
and mutating one merged value doesn't leak to another sibling
mapping that merged the same source.

### `_lastKeyQuoted` lifecycle

- Set by `_ParseMappingKey` / `_ParseFlowKey` on every entry.
- Read by the dispatch site **once**, immediately after key returns.
- No need to reset to a default — every key parse overwrites it.
- Not thread-safe, but YamlParser is single-threaded (one parser
  instance per `Parse` call).

### Flow-mapping merge

`_ParseFlowMapping` follows the same dispatch:

```z42
string key = this._ParseFlowKey();
bool keyQuoted = this._lastKeyQuoted;
this._SkipFlowWS();
// expect ':' ...
YamlValue v = this._ParseFlowValue();
if (key == "<<" && !keyQuoted) {
    this._ApplyMerge(m, v, mergedKeys, ref mergedCount, line, col);
} else if (m.ContainsKey(key)) {
    if (_IsMergedKey(mergedKeys, mergedCount, key)) {
        m.Set(key, v);
        _RemoveMergedKey(...);
    } else {
        throw this._Err("duplicate flow mapping key '" + key + "'");
    }
} else {
    m.Set(key, v);
}
```

Flow `_ParseFlowValue` already handles `[ ... ]` (flow sequence) and
`{ ... }` (flow mapping); we don't need to extend it to handle aliases
because — wait. Check: does `_ParseFlowValue` currently handle `*alias`?
Looking at the source, **no** — it treats `*` as part of a plain
scalar. This is a **pre-existing limitation** unrelated to merge keys.

**Sub-decision:** treat alias support in flow values as out of scope
for *this* spec (own pre-existing-bug spec if anyone asks). For
`<<: *anchor` inside a flow mapping, the workaround is `<<: [*a]` —
which doesn't help because that sequence itself is a flow sequence
whose items also go through `_ParseFlowValue`. So flow-mapping merge
**requires** a one-line extension to `_ParseFlowValue` to handle
`*alias`. We do this as part of this spec since it's necessary for
the flow-mapping scenario.

### `mergedKeys` storage

Parallel array `string[]` capped at 32 keys initial, growing as
needed. Reset per `_ParseBlockMapping` / `_ParseFlowMapping`
invocation (recursion-safe — each call frame has its own).

### Position tracking for errors

`_ApplyMerge` captures `line` / `col` at the `<<` key position before
parsing its value, so error messages point at the `<<` token rather
than the value position.

## Testing Strategy

- **New file:** `src/libraries/z42.yaml/tests/parse_merge_keys.z42`
  with `[Test]`-annotated functions for each Scenario in the spec.
- Coverage matrix:
  - Block mapping: single anchor merge, sequence merge, override-before,
    override-after, override-both-sides
  - Flow mapping: single anchor merge, mixed with explicit keys
  - Escape hatch: `"<<"` quoted, `'<<'` single-quoted
  - Direct mapping value (no alias)
  - Error paths: scalar value, null value, mixed sequence
  - Real-world: Docker Compose-style fragment
  - Interaction: per-doc scope reset (multi-doc), DeepClone independence
    (mutate one merged value → sibling unaffected)
  - Duplicate explicit-after-merge still errors
- Run via standard `./scripts/test-stdlib.sh z42.yaml`.
- Full GREEN: `./scripts/test-all.sh` before archive.

## Deferred / Future Work (in scope of this spec to record)

- **Re-emitting `<<:` in Stringify** — see Decision 6. If a user
  surfaces a use case (e.g. config-mutator tool that round-trips
  Helm charts), add provenance tracking then.
- **`<<` literal key without quoting** — won't fix. Quote-as-escape
  is the documented workaround everywhere.
