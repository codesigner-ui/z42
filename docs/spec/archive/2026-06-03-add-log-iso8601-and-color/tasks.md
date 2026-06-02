# Tasks: Log.* — ISO 8601 timestamps + TTY-aware color

> 状态：🟢 已完成 | 创建：2026-06-03 | 归档：2026-06-03 | 类型：stdlib feat

**变更说明：** Replace the `[LEVEL <unix-ms>] msg` log format with
`[LEVEL <ISO8601>] msg` and wrap the level token in ANSI color when
stderr is a TTY. Closes two diagnostics deferred items:
`diagnostics-future-iso8601-timestamps` (prereq:
`DateTime.ToIso8601()` ✅ shipped 2026-05-26) and
`diagnostics-future-color` (prereq: `Std.IO.Ansi` ✅ shipped
2026-06-01 by `add-console-ansi-color`).

**原因：** human-readable timestamps + visual level distinction in
terminals are the lowest-cost diagnostics upgrade for everyone
running z42 scripts. Pre-1.0 don't-care-about-compat: existing
Splunk / ELK pipelines configured for Unix ms must reconfigure.

**色彩映射：**
- TRACE → `Ansi.Dim` (gray)
- DEBUG → `Ansi.Cyan`
- INFO  → `Ansi.Green`
- WARN  → `Ansi.Yellow`
- ERROR → `Ansi.Red` + `Ansi.Bold`

**文档影响：**
- `src/libraries/z42.diagnostics/src/Log.z42` — format change
- `src/libraries/z42.diagnostics/README.md` — example output snippet
- `docs/design/stdlib/diagnostics.md` — mark both deferred items ✅

## Tasks
- [x] 1.1 z42: extract `FormatHeader(int level, DateTime now) → string`
      public helper. Returns `"[" + colorLevel(level) + " " +
      now.ToIso8601() + "]"`. Color disabled-or-not is decided by
      `Ansi.Enabled()` which itself auto-detects via
      `ConsoleError.IsTerminal()` + `NO_COLOR` — caller doesn't
      micro-manage.
- [x] 1.2 z42: update `Write` / `WriteF` to use `FormatHeader` and
      drop the inline `[NAME <ms>]` construction
- [x] 1.3 z42: helper `_colorLevel(int level, string name)` returns
      raw `name` if `Ansi.Enabled() == false`, else
      `Ansi.<color>(name)`. Special-case ERROR = `Ansi.Bold(
      Ansi.Red(name))` so the brightest level pops the most.
- [x] 1.4 Note: Log writes to **stderr**, but `Ansi.Enabled()` checks
      `Console.IsTerminal()` (stdout). For the common case
      (interactive shell), both are TTY at the same time, so this is
      ~always right. If a script pipes stdout to a file but leaves
      stderr at the tty, colors won't appear in logs — acceptable
      v0 tradeoff (matches `colored` Rust crate default).
- [x] 1.5 Tests: extend `log_basic.z42` or new `log_format.z42`:
      - `FormatHeader` with `Ansi.SetEnabled(false)` produces
        `[LEVEL ISO8601]` — no escape codes
      - `FormatHeader` with `Ansi.SetEnabled(true)` produces output
        containing ESC[33m for WARN (yellow), ESC[31m + ESC[1m for
        ERROR (red + bold), etc.
      - ISO 8601 timestamp matches `YYYY-MM-DDTHH:MM:SS.sssZ` shape
        (regex test or shape probe)
- [x] 1.6 Doc sync: README example + diagnostics.md Deferred entries
- [x] 1.7 `./scripts/test-all.sh` — full GREEN
- [x] 1.8 Archive + commit + push
