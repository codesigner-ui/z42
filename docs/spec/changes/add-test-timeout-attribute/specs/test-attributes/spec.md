# Spec: `[Timeout(milliseconds)]` test attribute

## ADDED Requirements

### Requirement: attribute parser accepts integer literals as named-arg values

#### Scenario: integer-literal value parses to AttributeArgInt

- **WHEN** an attribute body is parsed as `[Skip(reason: "stub", count: 3)]`
- **THEN** `TestAttribute.NamedArgs["reason"]` is `AttributeArgString("stub")`
- **AND** `TestAttribute.NamedArgs["count"]` is `AttributeArgInt(3)`

#### Scenario: string-literal value preserves existing behaviour

- **WHEN** an attribute body uses only string-literal values
  (`[Skip(reason: "ios bug", platform: "ios")]`)
- **THEN** the dictionary contents are `AttributeArgString` for both
  keys, byte-equal to the pre-change parser output (no consumer-level
  behavioural diff)

#### Scenario: existing string-arg consumers see a clear message on type mismatch

- **WHEN** a `[Skip(reason: 42)]` is parsed (int where existing
  consumer expects string)
- **THEN** the compiler emits an existing diagnostic
  (`E0914 SkipReasonMissing` — message "reason must be a string
  literal") via the shared `RequireStringArg(key)` helper

### Requirement: `[Timeout(milliseconds: <int>)]` attribute on `[Test]` / `[Benchmark]` methods

#### Scenario: well-formed timeout binds and serialises

- **WHEN** a method is annotated `[Test]` and `[Timeout(milliseconds: 5000)]`
  (attribute order is not significant)
- **THEN** the resulting `TestEntry.TimeoutMs` is `5000`
- **AND** the value is serialised into the zbc TIDX section as a
  per-entry `timeout_ms: i32` field
- **AND** the runner observes `DiscoveredTest.timeout_ms = Some(5000)`

#### Scenario: missing `milliseconds:` named arg is rejected

- **WHEN** the attribute is `[Timeout]` (no parens) or `[Timeout()]`
  (empty parens)
- **THEN** the compiler emits **E0916 `TimeoutValueInvalid`** with
  message `[Timeout] requires a single named arg "milliseconds: <int>"`

#### Scenario: attribute on a non-`[Test]` / non-`[Benchmark]` method is rejected

- **WHEN** the method has `[Timeout(milliseconds: 1000)]` but neither
  `[Test]` nor `[Benchmark]`
- **THEN** the compiler emits **E0916** with message
  `[Timeout] requires [Test] or [Benchmark] on the same method`

#### Scenario: zero / negative milliseconds is rejected

- **WHEN** the value is `0` or negative literal
- **THEN** the compiler emits **E0916** with message
  `[Timeout] milliseconds must be > 0 (got <value>)`

#### Scenario: value that overflows i32 is rejected

- **WHEN** the value > `i32::MaxValue` (2 147 483 647 ms ≈ 24.8 days)
- **THEN** the compiler emits **E0916** with message
  `[Timeout] milliseconds must fit in i32 (got <value>)`

#### Scenario: non-integer argument is rejected

- **WHEN** the value is a string literal (`milliseconds: "5000"`) or
  any non-integer literal
- **THEN** the compiler emits **E0916** with message
  `[Timeout] milliseconds must be an integer literal (got string)`

#### Scenario: duplicate `[Timeout]` on one method is rejected

- **WHEN** a method carries two `[Timeout(...)]` attributes
- **THEN** the compiler emits **E0916** with message
  `[Timeout] applied more than once on the same method`

### Requirement: test runner honours the per-method override

#### Scenario: method with `[Timeout(50)]` and a 200 ms sleep is killed at 50 ms

- **WHEN** the test method body sleeps 200 ms and the test entry
  has `timeout_ms = 50`
- **THEN** the runner kills z42vm at approximately 50 ms and
  reports `Outcome::Failed { reason: "timed out after 0.05s …" }`

#### Scenario: method without `[Timeout]` uses the runner default

- **WHEN** the test entry has `timeout_ms = 0` (sentinel for "no
  override")
- **THEN** the runner applies its built-in default
  (`TEST_TIMEOUT_SECS = 300` s)

#### Scenario: per-method override is clamped to a hard ceiling

- **WHEN** the attribute requests `[Timeout(86_400_000)]` (1 day)
- **THEN** the runner clamps to `2 × TEST_TIMEOUT_SECS = 600 s` and
  uses that ceiling, with a one-line diagnostic
  `note: clamped requested timeout 86400000 ms to ceiling 600000 ms`
  so a typo can't disable the safety net

### Requirement: zbc + zpkg minor versions are bumped together

#### Scenario: writer and reader minor agree post-change

- **WHEN** any zbc artifact is written by the post-change `ZbcWriter`
- **THEN** `ZbcWriter.VersionMinor` matches Rust's `ZBC_VERSION_MINOR`
  exactly (strict-pin policy)
- **AND** `ZpkgWriter.VersionMinor` matches `ZPKG_VERSION_MINOR`
  similarly (zpkg couples with zbc per `.claude/rules/version-bumping.md`)

#### Scenario: format invariant tests + fixture regen pass

- **WHEN** the contributor follows the bump checklist
- **THEN** `dotnet test --filter "FullyQualifiedName~Z42.Tests.Zbc"`
  passes
- **AND** `dotnet test --filter "FullyQualifiedName~Z42.Tests.Zpkg"`
  passes
- **AND** `./src/tests/zbc-format/generate-fixtures.sh` regenerates
  the 6 source.zbc + expected.json pairs without errors
- **AND** `./src/tests/zpkg-format/generate-fixtures.sh` similarly

## IR Mapping

`[Timeout(milliseconds)]` extends `TestEntry` in TIDX section:

```
TestEntry (per method) {
  ... existing fields ...
  + timeout_ms: i32          // 0 = use runner default; >0 = explicit override
}
```

Sentinel `0` chosen so entries without the attribute occupy the
default-zeroed bytes (no per-entry branching at write time for the
common case).

## Pipeline Steps

受影响的 pipeline 阶段（按顺序）：

- [ ] Lexer — N/A (attribute syntax already supported)
- [x] Parser / AST — `[Timeout(int)]` and `[Timeout(milliseconds: int)]`
  bind to existing `AttributeNode` (parser already accepts attributes
  with positional + named args)
- [x] TypeChecker — `TestAttributeValidator` extension + E0915
  diagnostic
- [x] IR Codegen — `BoundTestAttribute → TestEntry.TimeoutMs` write
- [x] VM interp — N/A (TIDX is read by the test runner, not interp)
- [x] zbc / zpkg writer + reader — version minor bump + new TIDX slot
- [x] z42-test-runner — per-test budget application + clamp
