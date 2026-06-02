# Proposal: add `Std.Time.DateTimeOffset`

## Why

`DateTime` (UTC instant) and `TimeZone` (offset/name) ship since
`add-z42-time` / `add-timezone-basics`. The pair (UTC instant +
which zone it was authored in) is the standard unit BCL / Java /
Go expose as `DateTimeOffset` / `OffsetDateTime` / `time.Time` —
the canonical answer to "what time was this and where?".

Today callers hand-roll `(DateTime utc, TimeZone tz)` tuples and
have to format with `DateTime.ToIso8601With(tz)` and recompute local
field accessors via `utc.AddMinutes(tz.OffsetMinutes()).Year()`-style
arithmetic. Listed in `docs/design/stdlib/time.md` Deferred as
`time-future-datetime-offset`. No blocker — pure script over
existing primitives.

## What Changes

1. **New public class `Std.Time.DateTimeOffset`** holding `(DateTime
   utc, TimeZone tz)` and exposing local-wall-clock accessors:
   - `DateTimeOffset(DateTime utc, TimeZone tz)` constructor
   - `static DateTimeOffset Now(TimeZone tz)` — `DateTime.UtcNow()` + tz
   - `static DateTimeOffset FromLocal(int year, int month, int day,
     int hour, int minute, int second, int millisecond, TimeZone tz)`
     — interprets the field values as wall-clock in `tz`, computes
     the UTC instant by subtracting the offset
   - `static DateTimeOffset Parse(string iso8601)` — parses ISO 8601
     with mandatory offset suffix (`Z` / `±HH:MM` / `±HHMM`); UTC
     instant via existing `DateTime.ParseIso8601`, offset re-extracted
     from the suffix and wrapped in a `TimeZone.FromOffsetMinutes`
   - `UtcDateTime() → DateTime` — the stored UTC moment
   - `LocalDateTime() → DateTime` — UTC + offset, for accessing
     wall-clock fields
   - `Offset() → TimeZone` — the stored offset
   - `Year/Month/Day/Hour/Minute/Second/Millisecond/DayOfWeek/
     DayOfYear` — local accessors (delegate to `LocalDateTime()`)
   - `ToIso8601() → string` — renders via `UtcDateTime().ToIso8601With(Offset())`
   - `override string ToString()` — same as ToIso8601
   - `Equals(DateTimeOffset other)` — UTC-only (same instant, even if
     authored in different zones) — matches BCL `Equals`
   - `EqualsExact(DateTimeOffset other)` — UTC AND offset both match
     — matches BCL `EqualsExact`
   - `IsAfter / IsBefore(DateTimeOffset other)` — UTC instant
     comparison
   - `Subtract(DateTimeOffset other) → TimeSpan` — UTC-instant diff
   - `Add(TimeSpan span) → DateTimeOffset` — shifts UTC, preserves
     offset
   - `SubtractSpan(TimeSpan span) → DateTimeOffset`

2. **Tests** under `src/libraries/z42.time/tests/datetime_offset.z42`.

3. **Docs**: time.md Deferred entry → ✅; roadmap.md row update.

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.time/src/DateTimeOffset.z42` | NEW | the new class |
| `src/libraries/z42.time/tests/datetime_offset.z42` | NEW | tests |
| `src/libraries/z42.time/README.md` | MODIFY | add row for `DateTimeOffset.z42` |
| `docs/design/stdlib/time.md` | MODIFY | mark `time-future-datetime-offset` ✅ |
| `docs/design/stdlib/roadmap.md` | MODIFY | refine Deferred Backlog row |
| `docs/spec/changes/add-datetime-offset/` | NEW | this spec dir |

**只读引用**：

- `src/libraries/z42.time/src/DateTime.z42` — accessor + ParseIso8601 shape
- `src/libraries/z42.time/src/TimeZone.z42` — OffsetMinutes / FromOffsetMinutes
- `src/libraries/z42.time/src/TimeSpan.z42` — Add/Subtract overloads

## Out of Scope

- **IANA-named timezone (`America/New_York`)** — still
  `time-future-tzdata-iana`. Fixed-offset zones cover the canonical
  `DateTimeOffset` use case; DST-aware named zones are a separate
  axis.
- **`Std.Time.DateOnly` / `TimeOnly`** — separate classes if
  needed.
- **strftime format strings** — still
  `time-future-format-parse` portion.

## Open Questions

- [ ] **Parse without offset suffix**: BCL `DateTimeOffset.Parse`
  assumes local zone if no offset present; we have no concept of
  "local zone" at the language level. Throw `FormatException`
  pointing at "missing offset" — caller can fall back to
  `DateTime.ParseIso8601` + explicit `TimeZone.Utc()`.
- [ ] **`Equals(null)`**: BCL throws on `Equals(object)` with `null`
  but `Equals(DateTimeOffset)` returns false (struct semantics).
  Going with **return false** for null arg — matches the pattern in
  z42.net's `IPEndPoint.Equals`.
