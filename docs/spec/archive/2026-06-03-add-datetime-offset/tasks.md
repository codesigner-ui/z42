# Tasks: add Std.Time.DateTimeOffset

> 状态：🟢 已完成 (with workaround) | 创建：2026-06-02 | 落地：2026-06-03 | 类型：stdlib feat

## 落地概览

Initially blocked 2026-06-02 by a z42 TypeChecker E0402 bug that
fires on **new `.z42` source files in the `z42.time` package** when
they reference sibling cross-file types (`TimeZone`, `TimeSpan`).
Workaround applied 2026-06-03: instead of `src/DateTimeOffset.z42`,
the new class was appended to the **existing** `src/DateTime.z42`
file. Tests still live in a new file (`tests/datetime_offset.z42`)
— test files don't trigger the bug, only src files do.

Probe confirmed scope of the bug — minimal new `src/_BugProbe.z42`
containing only:

```z42
namespace Std.Time;
public sealed class _BugProbe {
    public static string Test() {
        DateTime dt = DateTime.UnixEpoch();
        TimeZone tz = TimeZone.Utc();
        return dt.ToIso8601With(tz);
    }
}
```

…fails to compile with `E0402: argument 1: expected TimeZone, got
TimeZone`. The bug is **package-specific** to `z42.time` (parallel
attempt to add a new file to `z42.net` succeeded — see
`add-ipendpoint-wrapper`). Suspected root cause: recent metadata
refactors (`272b0115` `Box<[Value]>` / `06b57853` `NameIndex`)
introduced an identity divergence when a new file gets compiled
against `z42.time`'s already-built sibling type metadata. Tracked
separately as a TypeChecker fix priority (not blocking shippable
features anymore now that workaround exists).

## API

```z42
public sealed class DateTimeOffset {
    public DateTimeOffset(DateTime utc, TimeZone tz);
    public static DateTimeOffset Now(TimeZone tz);
    public static DateTimeOffset FromLocal(int y, int mo, int d,
                                           int h, int mi, int s, int ms, TimeZone tz);
    public static DateTimeOffset Parse(string iso8601);

    public DateTime UtcDateTime();
    public DateTime LocalDateTime();
    public TimeZone Offset();
    public int Year/Month/Day/Hour/Minute/Second/Millisecond/DayOfWeek/DayOfYear();
    public string ToIso8601();
    public override string ToString();

    public bool Equals(DateTimeOffset other);         // UTC-only
    public bool EqualsExact(DateTimeOffset other);    // UTC + offset
    public bool IsAfter(DateTimeOffset other);
    public bool IsBefore(DateTimeOffset other);

    public TimeSpan Subtract(DateTimeOffset other);
    public DateTimeOffset Add(TimeSpan span);
    public DateTimeOffset SubtractSpan(TimeSpan span);
}
```

## Tasks

- [x] 1.1 Append `DateTimeOffset` class to existing
      `src/libraries/z42.time/src/DateTime.z42` (workaround for the
      package-specific E0402 bug — see "落地概览")
- [x] 1.2 Constructor + null validation
- [x] 1.3 Factories `Now(tz)` / `FromLocal(...)` / `Parse(iso8601)`
- [x] 1.4 Accessors `UtcDateTime` / `LocalDateTime` / `Offset` +
      local field accessors
- [x] 1.5 `ToIso8601` / `ToString` round-trip
- [x] 1.6 Equality `Equals` (UTC-only) / `EqualsExact` (UTC + offset)
- [x] 1.7 Arithmetic `Subtract` / `Add(TimeSpan)` / `SubtractSpan`
- [x] 1.8 Tests in new `tests/datetime_offset.z42` — 21 scenarios
      covering construct / null / from-local / parse / round-trip /
      equality / arithmetic / Now
- [x] 1.9 `./scripts/test-all.sh` — full GREEN
- [x] 1.10 Doc sync: `docs/design/stdlib/time.md` Deferred entry
- [x] 1.11 Commit + push

## 备注

- The `add-ipendpoint-wrapper` spec succeeded as a new file in
  `z42.net` — the bug is z42.time-specific, not a general
  "new files" issue. Diagnosing further would require source-level
  comparison of how z42c walks the two packages' type metadata.
- Should a TypeChecker fix land in the future, the class can be
  moved to its own file (`src/DateTimeOffset.z42`) without API
  churn — pure file-organization refactor.
