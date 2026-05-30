# Spec: `Std.Test.Assert` — numeric & array helpers

## ADDED Requirements

### Requirement: numeric ordering assertions

#### Scenario: `Greater(long, long)` passes when actual is strictly greater
- **WHEN** `Assert.Greater(5L, 3L)` is called
- **THEN** the call returns without throwing

#### Scenario: `Greater(long, long)` fails on equal or lesser values
- **WHEN** `Assert.Greater(3L, 3L)` (equal) or `Assert.Greater(3L, 5L)` (lesser) is called
- **THEN** a `TestFailure` is thrown
- **AND** its message contains `"expected 3 > 3"` (or `"3 > 5"`)
- **AND** `e.Actual == "3"` and `e.Expected` contains `"> 3"` (or `"> 5"`)

#### Scenario: `Less` / `GreaterOrEqual` / `LessOrEqual` follow symmetric semantics
- **GreaterOrEqual** passes for equal values; **LessOrEqual** passes for equal values
- **Less** fails for equal values (strict)
- Message format mirrors `Greater`

#### Scenario: double overloads follow the same rules
- **WHEN** any of `Greater/Less/GreaterOrEqual/LessOrEqual` is called with `double` arguments
- **THEN** the double overload resolves and behaves identically to the long overload

#### Scenario: NaN in any double overload triggers failure
- **WHEN** `Assert.Greater(Double.NaN, 0.0)` (or any combination with NaN) is called
- **THEN** a `TestFailure` is thrown with a message indicating the NaN-vs-comparison issue
- **AND** the assertion never silently passes due to IEEE-754's all-comparisons-false rule

### Requirement: range assertion (`InRange`)

#### Scenario: value within inclusive bounds passes
- **WHEN** `Assert.InRange(5L, 0L, 10L)` is called
- **THEN** the call returns without throwing

#### Scenario: boundary values are included
- **WHEN** `Assert.InRange(0L, 0L, 10L)` or `Assert.InRange(10L, 0L, 10L)` is called
- **THEN** the call returns without throwing (inclusive bounds)

#### Scenario: value outside bounds fails with structured message
- **WHEN** `Assert.InRange(-1L, 0L, 10L)` is called
- **THEN** a `TestFailure` is thrown
- **AND** its message contains `"expected -1 in [0, 10]"`
- **AND** `e.Actual == "-1"` and `e.Expected == "[0, 10]"`

### Requirement: array-element containment

#### Scenario: `Contains(object, object[])` passes when needle present
- **WHEN** `Assert.Contains(2, [1, 2, 3])` is called (`object[]` literal)
- **THEN** the call returns without throwing

#### Scenario: `Contains(object, object[])` fails when needle absent
- **WHEN** `Assert.Contains(4, [1, 2, 3])` is called
- **THEN** a `TestFailure` is thrown
- **AND** its message contains `"array does not contain expected element"`
- **AND** `e.Expected == "4"`

#### Scenario: existing string-string `Contains` unchanged
- **WHEN** `Assert.Contains("foo", "foobar")` is called (the pre-spec overload)
- **THEN** behavior matches pre-spec verbatim — neither signature nor message changes

#### Scenario: `DoesNotContain` is the inverse
- **WHEN** `Assert.DoesNotContain(4, [1, 2, 3])` passes; `DoesNotContain(2, [1, 2, 3])` fails
- **THEN** symmetric to `Contains`

### Requirement: array emptiness

#### Scenario: `IsEmpty(object[])` passes on length-0 array
- **WHEN** `Assert.IsEmpty(new object[]{})` is called
- **THEN** the call returns without throwing

#### Scenario: `IsEmpty(object[])` fails with structured message on non-empty
- **WHEN** `Assert.IsEmpty(new object[]{1})` is called
- **THEN** a `TestFailure` is thrown
- **AND** message contains `"expected empty array but length = 1"`
- **AND** `e.Actual == "1"` and `e.Expected == "0"`

#### Scenario: `IsNotEmpty(object[])` is inverse
- `IsNotEmpty([1])` passes; `IsNotEmpty([])` fails

## MODIFIED Requirements

(none — `Std.Test.Assert` existing API surface unchanged)

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [ ] VM interp
- [ ] Test runner
- [x] **Stdlib library `z42.test`**

Pure library extension; no compiler / runtime / runner changes.
