# Spec: Generic Attribute Syntax

## ADDED Requirements

### Requirement: Parser accepts `[Name<TypeName>]`

Z42 parser recognises a single-type-parameter generic suffix on a `z42.test.*` attribute.

#### Scenario: ShouldThrow with type arg parses
- **WHEN** source contains `[ShouldThrow<TestFailure>]` directly above a `[Test]` function
- **THEN** parser produces a `TestAttribute(Name="ShouldThrow", TypeArg="TestFailure", NamedArgs=null)` node
- **AND** the `[Test]` attribute on the same function is also collected

#### Scenario: Type arg coexists with named args
- **WHEN** source contains `[X<E>(reason: "...")]` (hypothetical future use)
- **THEN** parser produces a `TestAttribute(Name="X", TypeArg="E", NamedArgs={reason: "..."})` node
- **AND** no parse error is raised at the syntax layer (semantic check is in validator)

#### Scenario: Bare attribute without type arg
- **WHEN** source contains `[Test]` (no `<>`)
- **THEN** parser produces a `TestAttribute(Name="Test", TypeArg=null, NamedArgs=null)` node
- **AND** behaviour is identical to before R4.B

#### Scenario: Multi-type-arg unsupported
- **WHEN** source contains `[X<A, B>]`
- **THEN** parser raises `ParseException` with code `E0202` (UnexpectedToken) at the comma
- **AND** message indicates only single type parameters are accepted in attributes

#### Scenario: Nested generic unsupported
- **WHEN** source contains `[X<List<int>>]`
- **THEN** parser raises `ParseException` at the inner `<`
- **AND** message indicates nested generic type parameters are not supported in attributes

### Requirement: ShouldThrow attribute is recognised

`ShouldThrow` is added to `TestAttributeNames` whitelist.

#### Scenario: ShouldThrow is a known z42.test attribute
- **WHEN** parser encounters `[ShouldThrow<E>]`
- **THEN** it dispatches into `ParseTestAttributeBody` (not silently skipped as "unknown attribute")

### Requirement: Validator E0913 — ShouldThrow type validation

`TestAttributeValidator` enforces three rules.

#### Scenario: ShouldThrow without type arg
- **WHEN** function has `[ShouldThrow]` (no `<E>`)
- **THEN** diagnostic `E0913` is reported on the attribute span
- **AND** message: `` `[ShouldThrow]` requires a single type argument naming the expected exception type (e.g. `[ShouldThrow<TestFailure>]`) ``

#### Scenario: ShouldThrow type does not exist
- **WHEN** function has `[ShouldThrow<NotAType>]` and `NotAType` is not declared anywhere visible
- **THEN** diagnostic `E0913` is reported
- **AND** message: `` `[ShouldThrow<NotAType>]` references unknown type `NotAType` ``

#### Scenario: ShouldThrow type does not extend Exception
- **WHEN** function has `[ShouldThrow<int>]` (or any non-Exception type)
- **THEN** diagnostic `E0913` is reported
- **AND** message: `` `[ShouldThrow<int>]` type must derive from `Exception` ``

#### Scenario: ShouldThrow without [Test] / [Benchmark]
- **WHEN** function has `[ShouldThrow<E>]` but neither `[Test]` nor `[Benchmark]`
- **THEN** diagnostic `E0914` is reported (existing modifier-without-primary rule, extended to ShouldThrow)
- **AND** message indicates `[ShouldThrow]` is a modifier and requires a primary test attribute

#### Scenario: Type arg on non-ShouldThrow attribute
- **WHEN** function has `[Test<TestFailure>]` (or any non-ShouldThrow attribute with type arg)
- **THEN** diagnostic `E0913` is reported
- **AND** message: `` `[Test]` does not accept a type argument; `<...>` syntax is reserved for `[ShouldThrow<E>]` ``

### Requirement: IrGen writes ExpectedThrowTypeIdx

When `[ShouldThrow<E>]` passes validation, IrGen records the type name in TIDX.

#### Scenario: ShouldThrow round-trips through TIDX
- **GIVEN** a function with `[Test]` + `[ShouldThrow<TestFailure>]`
- **WHEN** the file is compiled to .zbc
- **THEN** the TIDX section's `TestEntry.expected_throw_type_idx` is non-zero
- **AND** the resolved string in the loaded artifact equals `"TestFailure"` (short name; namespace-qualification handled by future runner if needed)
- **AND** `TestFlags::SHOULD_THROW` bit is set

## IR Mapping

R4.B does not introduce new IR instructions. It populates an existing TIDX field:

- `TestEntry.ExpectedThrowTypeIdx` (C# `int`, Rust `u32`) — already present in v=2 format
- `TestFlags::SHOULD_THROW` (bit 2) — already defined in TIDX flags bitset

## Pipeline Steps

- [x] Lexer — no changes
- [ ] Parser / AST — add `TypeArg` field to `TestAttribute`; extend `ParseTestAttributeBody`
- [ ] TypeChecker — no direct changes (TestAttributeValidator runs separately)
- [x] IR Codegen — populate `ExpectedThrowTypeIdx` (was hard-coded `0`)
- [x] VM interp — no changes
- [x] Loader (Rust) — already resolves `expected_throw_type` string in `resolve_test_index_strings`
