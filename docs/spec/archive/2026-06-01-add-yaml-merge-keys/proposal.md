# Proposal: add YAML merge keys (`<<: *anchor`)

## Why

YAML merge keys (`<<:` referencing an anchor) are the single most
common YAML extension users hit in the wild — Docker Compose, Helm
charts, Kubernetes manifests, GitHub Actions reusable steps, and
Ansible inventory all use them to factor shared blocks. Example
(Docker Compose):

```yaml
x-common: &common
  restart: unless-stopped
  logging:
    driver: json-file

services:
  web:
    <<: *common
    image: nginx
  worker:
    <<: *common
    image: python
```

Without merge-key support, parsing real-world YAML fails the first
time a user points z42.yaml at production config.

History: merge keys are a YAML 1.1 spec extension (the
[`tag:yaml.org,2002:merge`](https://yaml.org/type/merge.html) language-
independent type). YAML 1.2 dropped them from the core spec but every
mainstream library still supports them by default (PyYAML, snakeyaml,
js-yaml, go-yaml, libyaml). We follow suit — pragmatic adoption of a
de-facto standard that surfaces in 90%+ of real-world infra YAML.

This is the second-to-last documented yaml gap. After this only
`yaml-future-complex-keys` (`? key` syntax — genuinely rare) remains.

## What Changes

1. **Block-mapping merge**: when a block mapping has an unquoted plain
   key `<<`, treat the value as a merge directive instead of a literal
   key. Value must resolve to a mapping or a sequence of mappings.
2. **Flow-mapping merge**: same semantics inside `{...}` flow mappings.
3. **Precedence rules** (matches YAML 1.1 spec + PyYAML / snakeyaml):
   - Explicit keys in the using mapping override merged keys
     **regardless of source position** (before or after the `<<:`).
   - Within a sequence of merge sources, **earlier sources override
     later** ones.
   - Multiple explicit occurrences of the same non-merge key remain
     errors.
4. **Quoted `<<` stays literal**: `"<<": foo` and `'<<': foo` produce
   a regular string key `<<` (escape hatch for users who need it).
5. **Round-trip semantics**: `<<` is a parse-time transform.
   `Stringify` emits the expanded mapping — no `<<:` token in output.
   Matches PyYAML `safe_load` + `dump` round-trip.
6. **Error cases**:
   - `<<:` value resolves to non-mapping (scalar, null, mixed
     sequence) → `YamlException` with the offending position.
   - Existing undefined-alias error keeps working through the alias
     dispatch path; no new error code needed.
7. **No new public API surface**: `YamlValue.Parse` / `ParseAll` /
   stream overloads transparently expand merges; `Stringify`
   transparently does not re-emit them.
8. **Doc + roadmap sync**:
   - `docs/design/stdlib/yaml.md`: add merge-keys section under
     "Supported syntax" + remove stale `fix-yaml-parse-regression`
     note (the alleged regression no longer reproduces — all 13 yaml
     test files green on `main` as of this spec).
   - `src/libraries/z42.yaml/README.md`: refresh "Scope (v0)" — most
     of the ❌ bullets shipped between 2026-05-25 and 2026-05-27.
   - `docs/roadmap.md` line 312: update the misleading "anchors /
     tags / multi-line / complex-keys / timestamps 仍延后" to reflect
     actual state (only `complex-keys` remains deferred).

## Scope

**修改 / 新增**：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.yaml/src/YamlParser.z42` | MODIFY | `_ParseBlockMapping` + `_ParseFlowMapping` merge dispatch; new `_ApplyMerge(dst, src, mergedKeys)` + `_ValidateMergeSource(val)` helpers; `_ParseMappingKey` gains a quoted-flag side channel (`_lastKeyQuoted`) |
| `src/libraries/z42.yaml/tests/parse_merge_keys.z42` | NEW | merge-key scenarios (basic, override-before, override-after, sequence-of-anchors, flow mapping merge, quoted-literal escape hatch, error paths, Docker Compose pattern) |
| `docs/design/stdlib/yaml.md` | MODIFY | "Supported syntax" table adds merge row; new "Merge keys (2026-05-31)" section under Implementation notes; mark `yaml-future-merge-keys` as shipped; **remove** stale `fix-yaml-parse-regression` paragraph |
| `src/libraries/z42.yaml/README.md` | MODIFY | refresh Scope bullets (anchors / tags / multi-line / multi-doc / timestamps / hex-octal / merge all ✅); add merge-key example |
| `docs/roadmap.md` | MODIFY | line 312 — replace "anchors / tags / multi-line / complex-keys / timestamps 仍延后" with the current truth (only `yaml-future-complex-keys` remains) |
| `docs/spec/changes/add-yaml-merge-keys/` | NEW | this spec dir (proposal / spec / design / tasks) |

**只读引用**：

- `src/libraries/z42.yaml/src/YamlValue.z42` — for `DeepClone()`,
  `Keys()`, `Get/Set/ContainsKey` API
- `src/libraries/z42.yaml/tests/parse_anchors_aliases.z42` — patterns
  for anchor-based test inputs
- `docs/spec/archive/2026-05-25-add-yaml-anchors-aliases/` — prior-art
  reference for dispatch wiring

## Out of Scope

- **Merge key in `Stringify` output** — round-trip preserves expanded
  form only. Re-emitting `<<:` would require tracking provenance in
  `YamlValue` (which key came from which merge source), and there's
  no real demand: configs that author with `<<:` are read by tools,
  not re-emitted by them.
- **Deep merge** — values are inserted shallowly. A merged key whose
  value is a mapping is replaced wholesale by an explicit key with a
  mapping value; we don't recursively combine. Matches PyYAML default.
- **Merge in non-mapping contexts** — `<<` in a sequence item or
  scalar context is undefined; falls through to existing dispatch
  (treated as literal `<<` string).
- **Complex keys (`? key`)** — unrelated; stays in `yaml-future-complex-keys`.

## Open Questions

- [ ] **Direct mapping as merge value** (`<<: {a: 1, b: 2}` or block
  mapping continuation): spec text says the value "must be a mapping
  node or sequence of mapping nodes", which permits direct mappings.
  PyYAML supports this. Default: **accept** — costs nothing extra
  once the validator checks `value.IsMapping()`.
- [ ] **`<<: null`** as no-op vs error: PyYAML errors. We follow
  PyYAML — clearer signal that the alias the user intended to write
  didn't actually exist / resolve.
