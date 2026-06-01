# Spec: Std.IO.Ansi color helpers

## ADDED Requirements

### Requirement: Color + style wrappers emit ANSI escapes when enabled

#### Scenario: Foreground color wrap
- **WHEN** `Ansi.SetEnabled(true)` followed by `Ansi.Red("error")`
- **THEN** the returned string is `"\x1b[31merror\x1b[0m"`.

#### Scenario: Bright foreground variant
- **WHEN** `Ansi.SetEnabled(true)` followed by `Ansi.BrightGreen("ok")`
- **THEN** the returned string is `"\x1b[92mok\x1b[0m"`.

#### Scenario: Style wrap
- **WHEN** `Ansi.SetEnabled(true)` followed by `Ansi.Bold("HEAD")`
- **THEN** the returned string is `"\x1b[1mHEAD\x1b[0m"`.

#### Scenario: Nested composition
- **WHEN** `Ansi.SetEnabled(true)` followed by
  `Ansi.Red(Ansi.Bold("X"))`
- **THEN** the returned string is `"\x1b[31m\x1b[1mX\x1b[0m\x1b[0m"`
  (terminals interpret the final reset as a no-op; visually correct).

### Requirement: Disabled mode passes input through unchanged

#### Scenario: Manual disable strips all wrappers
- **WHEN** `Ansi.SetEnabled(false)` followed by `Ansi.Red("error")`
- **THEN** the returned string is `"error"` (no escape codes).

#### Scenario: Disabled mode applies to every style/colour method
- **WHEN** `Ansi.SetEnabled(false)` followed by any of `Black`,
  `Yellow`, `BrightCyan`, `Bold`, `Dim`, `Italic`, `Underline`,
  `Reverse`
- **THEN** each returns its input string unchanged.

#### Scenario: SetEnabled override survives across calls
- **WHEN** `Ansi.SetEnabled(true)` is called, then `Ansi.Red("a")`
  emits a wrapped string, then no further `SetEnabled` is called,
  then `Ansi.Green("b")`
- **THEN** the second call also emits a wrapped string.

### Requirement: NO_COLOR env var auto-disables

#### Scenario: NO_COLOR set → Enabled() false
- **WHEN** `NO_COLOR` env var is set to any non-empty value AND
  `Ansi.SetEnabled` has NOT been called this run
- **THEN** `Ansi.Enabled()` returns `false` and wrappers pass
  through.

#### Scenario: Manual SetEnabled overrides NO_COLOR
- **WHEN** `NO_COLOR=1` is set in the env AND `Ansi.SetEnabled(true)`
  is called explicitly
- **THEN** `Ansi.Enabled()` returns `true` (manual override wins).

### Requirement: Strip removes any ANSI escape sequences

#### Scenario: Strip a single colour wrap
- **WHEN** `Ansi.Strip("\x1b[31merror\x1b[0m")`
- **THEN** returns `"error"`.

#### Scenario: Strip nested wraps
- **WHEN** `Ansi.Strip("\x1b[31m\x1b[1mX\x1b[0m\x1b[0m")`
- **THEN** returns `"X"`.

#### Scenario: Strip on plain text is identity
- **WHEN** `Ansi.Strip("no codes here")`
- **THEN** returns `"no codes here"` unchanged.

#### Scenario: Strip skips non-SGR escapes safely
- **WHEN** input contains a cursor-move sequence like `\x1b[2J` (clear
  screen — not an SGR colour code, ends in `J` not `m`)
- **THEN** Strip still removes it (anything between `\x1b[` and the
  next final byte in the `@-~` range is stripped).
