# Spec: YAML merge keys

## ADDED Requirements

### Requirement: Merge key dispatched on unquoted plain `<<`

#### Scenario: Single anchor merge in block mapping
- **WHEN** input is
  ```yaml
  defaults: &d
    a: 1
    b: 2
  prod:
    <<: *d
    c: 3
  ```
- **THEN** `Parse(input).Get("prod")` yields a mapping
  `{a: 1, b: 2, c: 3}` (3 keys, no `<<` key present).

#### Scenario: Explicit key after `<<:` overrides merged key
- **WHEN** input is
  ```yaml
  defaults: &d
    a: 1
    b: 2
  prod:
    <<: *d
    a: 99
  ```
- **THEN** `Parse(input).Get("prod").Get("a").AsInt() == 99` and the
  value for `b` is still `2`.

#### Scenario: Explicit key before `<<:` still wins
- **WHEN** input is
  ```yaml
  defaults: &d
    a: 1
    b: 2
  prod:
    a: 99
    <<: *d
  ```
- **THEN** `Parse(input).Get("prod").Get("a").AsInt() == 99` and the
  value for `b` is `2`.

#### Scenario: Sequence-of-anchors with earlier-wins precedence
- **WHEN** input is
  ```yaml
  base: &base
    a: 1
    b: 2
    c: 3
  override: &override
    b: 99
  combined:
    <<: [*override, *base]
  ```
- **THEN** `combined.Get("b").AsInt() == 99` (earlier `*override`
  wins) and `combined.Get("a").AsInt() == 1` (only in `*base`) and
  `combined.Get("c").AsInt() == 3`.

#### Scenario: Merge in flow mapping
- **WHEN** input is
  ```yaml
  defaults: &d
    a: 1
    b: 2
  prod: {<<: *d, c: 3}
  ```
- **THEN** `prod` parses as `{a: 1, b: 2, c: 3}`.

#### Scenario: Quoted `<<` stays a literal string key
- **WHEN** input is `"<<": special` inside a mapping
- **THEN** the resulting mapping has a string key `<<` with value
  `"special"` — no merge dispatch.

#### Scenario: Direct mapping as merge value (no alias)
- **WHEN** input is
  ```yaml
  prod:
    <<: {a: 1, b: 2}
    c: 3
  ```
- **THEN** `prod` yields `{a: 1, b: 2, c: 3}`.

### Requirement: Merge value type validation

#### Scenario: Merge alias resolves to scalar throws
- **WHEN** input is
  ```yaml
  scalar: &s 42
  bad:
    <<: *s
  ```
- **THEN** `Parse(input)` throws `YamlException` whose message
  mentions "merge" and the offending position.

#### Scenario: Merge sequence contains a non-mapping element throws
- **WHEN** input is
  ```yaml
  m: &m {a: 1}
  s: &s 42
  bad:
    <<: [*m, *s]
  ```
- **THEN** `Parse(input)` throws `YamlException`.

#### Scenario: Merge value is null throws
- **WHEN** input is `bad:\n  <<: ~\n`
- **THEN** `Parse(input)` throws `YamlException`.

### Requirement: Merge interacts correctly with other YAML features

#### Scenario: Docker Compose pattern
- **WHEN** input is
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
- **THEN** `services.web` has 3 keys (`restart`, `logging`, `image`)
  with `image == "nginx"`, and `services.worker` has 3 keys with
  `image == "python"`, and mutating `services.web.restart` does not
  affect `services.worker.restart` (alias DeepClone semantics carry
  through merge).

#### Scenario: Round-trip via Stringify drops the `<<` token
- **WHEN** parsed value is stringified back
- **THEN** the output mapping is the **expanded** form (no `<<:` in
  output text), parseable back into an equivalent value.

#### Scenario: Per-document merge scope
- **WHEN** input has two YAML docs separated by `---`, each with its
  own `&shared` anchor and `<<: *shared` reference
- **THEN** each doc's merge resolves against its own anchor table;
  `<<: *shared` in doc 2 does not see doc 1's anchor (matches existing
  per-doc anchor scope from `add-yaml-anchors-aliases`).

#### Scenario: Duplicate explicit key after merge still errors
- **WHEN** input is
  ```yaml
  d: &d {a: 1}
  bad:
    <<: *d
    a: 99
    a: 77
  ```
- **THEN** `Parse(input)` throws `YamlException` complaining of
  duplicate key `a` (explicit-vs-explicit duplicate — overriding a
  merged key once is fine, twice is the user's mistake).

## Pipeline Steps

- [x] Lexer — N/A (z42.yaml is pure-script; no language-level changes)
- [x] Parser / AST — N/A (parser is inside z42.yaml stdlib, not z42
      core parser)
- [ ] z42.yaml YamlParser — add merge-key detection + apply
- [ ] z42.yaml tests — new `parse_merge_keys.z42` covering 13+ scenarios
- [ ] docs/design/stdlib/yaml.md — section update + stale-note removal
- [ ] z42.yaml README — Scope refresh
- [ ] docs/roadmap.md — line 312 truth-up
