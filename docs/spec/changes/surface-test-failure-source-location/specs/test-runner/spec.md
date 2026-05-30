# Spec: Test runner — failure source location & stack trace surfacing

## ADDED Requirements

### Requirement: failure location extraction

#### Scenario: thrown exception with populated StackTrace
- **WHEN** a `[Test]` body throws a `Std.Exception` subclass and the interp's
  `populate_stack_trace` has filled `value.StackTrace` with a multi-line
  trace including a non-framework frame
- **THEN** the resulting `TestResult.failure_location` is `Some("<file>:<line>")`
  matching the first non-framework frame's location

#### Scenario: thrown exception with no user frames
- **WHEN** the stack trace contains only `Std.Test.*` / `.Assert.` frames
  (e.g. stdlib self-tests where Assert framework throws inside its own code)
- **THEN** `failure_location` is `None` but `stack_trace` is `Some(full)`

#### Scenario: thrown value without StackTrace field
- **WHEN** a test throws `Value::Str("oops")` or a non-Exception object
- **THEN** `failure_location` is `None` and `stack_trace` is `None`
- **AND** `reason` falls back to the message-only or debug-format string
  (preserving pre-spec behavior)

#### Scenario: Exception with null StackTrace field
- **WHEN** an Exception object exists but its `StackTrace` field was never
  populated (Phase 1 corner: throw site was a non-instrumented path)
- **THEN** behave as "no stack" — `failure_location: None`, `stack_trace: None`

### Requirement: framework-frame filter for primary location

#### Scenario: frames matching framework patterns are skipped
- **WHEN** a stack frame's `func_name` starts with `"Std.Test."` or contains
  `".Assert."`
- **THEN** that frame is excluded from primary-location consideration
- **AND** the first remaining frame becomes the primary location

#### Scenario: user code is preserved
- **WHEN** a stack frame's `func_name` is `"MyTests.test_arithmetic"` (no
  framework markers)
- **THEN** that frame's `(file:line)` is the primary location

### Requirement: Outcome / TestResult shape

**ADDED:**

```rust
pub enum Outcome {
    Passed { duration_ms: u64 },
    Failed {
        reason: String,
        location: Option<String>,
        stack_trace: Option<String>,
    },
    Skipped { reason: String },
}

pub struct TestResult {
    pub name: String,
    pub status: TestStatus,
    pub duration_ms: u64,
    pub reason: Option<String>,                         // unchanged
    pub failure_location: Option<String>,               // NEW
    pub stack_trace: Option<String>,                    // NEW
}
```

`TestResult::from_outcome` populates the new fields from `Outcome::Failed`'s
new arms; pre-existing skip / pass arms leave them `None`.

### Requirement: pretty formatter surfaces location & stack

#### Scenario: failed test with location and stack
- **WHEN** rendering a `TestResult` with `failure_location = Some("f.z42:7")`
  and `stack_trace = Some(s)`
- **THEN** output is:

```
  ✗ <name>  (f.z42:7)
      <reason line 1>
      stack:
        <stack line 1>
        <stack line 2>
```

#### Scenario: failed test with no location
- **WHEN** `failure_location` is `None` but `reason` is present
- **THEN** output matches the pre-spec layout (single line, then indented
  reason) — no `(loc)` suffix, no `stack:` block

### Requirement: TAP formatter YAML block

#### Scenario: failure with location and stack
- **WHEN** producing TAP for a `TestResult` with both fields set
- **THEN** YAML block includes `location: '<loc>'` and `stack: |` literal
  block with the trace indented under it
- **AND** `message:` key is unchanged (contains `reason` only)

#### Scenario: failure with no location
- **WHEN** rendering a TestResult without `failure_location` or
  `stack_trace`
- **THEN** YAML block contains only the existing `message:` key (pre-spec
  output preserved verbatim)

### Requirement: JSON formatter fields

#### Scenario: failed test JSON output
- **WHEN** serialising a `TestResult` with the new fields set
- **THEN** the JSON object contains `"failure_location": "<loc>"` and
  `"stack_trace": "<multi-line trace>"` alongside the existing
  `"reason"` field

#### Scenario: omit-when-none invariant
- **WHEN** the new fields are `None`
- **THEN** they are omitted from JSON output (
  `#[serde(skip_serializing_if = "Option::is_none")]`)

## MODIFIED Requirements

### Requirement: `runner::format_value`

**Before:** Reads only `Message` field from a thrown Object; returns a
single composite string. Stack trace, even when present, is dropped.

**After:** Replaced by `format_failure_with_stack(val, module) ->
FailureDetails`. New helper:
- Reads `Message` field (same as before) for `message`
- Calls `exception::read_stack_trace(val, module)` for `stack_trace`
- Calls `first_user_frame(&stack)` for `primary_location` (None when no
  stack or no user frame)
- Callers (`classify_thrown`, `classify_should_throw`, Setup/Teardown
  failure branches) construct `Outcome::Failed { reason: details.message,
  location: details.primary_location, stack_trace: details.stack_trace }`
  instead of the single-string form.

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [ ] VM interp
- [x] **Test runner — execution (`runner.rs::format_value` + classify branches)**
- [x] **Test runner — result types (`result.rs::Outcome` + `TestResult`)**
- [x] **Test runner — formatters (`pretty.rs` / `tap.rs` / `json.rs`)**

No compiler-side / VM-runtime changes. Pure consumer-side upgrade —
`Exception.StackTrace` is already populated by the runtime since 2026-05-10.
