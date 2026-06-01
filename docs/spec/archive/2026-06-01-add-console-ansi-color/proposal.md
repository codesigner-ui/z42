# Proposal: add Std.IO.Ansi color helpers

## Why

z42 has a growing pile of script-style entry points (`scripts/regen-
golden-tests.z42`, `scripts/test-vm.z42`, `scripts/test-cross-zpkg.
z42`, `scripts/build-stdlib.z42`, …) that all emit progress / pass /
fail output through `Console.WriteLine`. Today none of them can
distinguish a passed test from a failed one in the terminal output —
the user has to read every line. The test-runner under
`src/runtime/.../test_runner.rs` has the same gap.

Bash side already uses ANSI codes liberally (`scripts/test-all.sh`
prints colored stage headers). When we eventually port `test-all.sh`
to z42 (memory note `project_scripts_z42_port.md` lists this as the
last-large bash file), the z42 port will look strictly worse than
the bash original unless we have an ANSI helper in stdlib.

The piece is also blocking nothing — `Console.IsTerminal()` already
exists (extend-z42-io-script-helpers, 2026-05-16) for the TTY check,
and `Environment.GetEnvironmentVariable` exists for `NO_COLOR`.
Roadmap and `project_scripts_z42_port.md` both flag `Console.
WriteAnsi` colour helpers as P3 / "未做", so this clears the last
unblocked P3 item on the script-port path.

## What Changes

1. **New `Std.IO.Ansi` static class** in z42.io. Single class, raw
   wrappers — `Ansi.Red("error")` returns the input string wrapped in
   `\x1b[31m…\x1b[0m` when ANSI is enabled, or the bare string when
   disabled. Caller composes by nesting (`Ansi.Red(Ansi.Bold("x"))`).
2. **Auto-detect on first call**: enabled iff `Console.IsTerminal()`
   is true AND `NO_COLOR` env var is unset (de-facto standard, see
   [no-color.org](https://no-color.org)). Cached after first check
   so repeated calls in a script loop don't re-stat the tty.
3. **Manual override**: `Ansi.SetEnabled(bool)` for scripts that
   want to force-enable (e.g. piping into `less -R`) or force-disable
   (CI logs with weird parsers).
4. **Foreground palette**: Black / Red / Green / Yellow / Blue /
   Magenta / Cyan / White (codes 30–37). Bright variants (90–97)
   under `Bright*` prefix.
5. **Styles**: Bold / Dim / Italic / Underline / Reverse (codes
   1 / 2 / 3 / 4 / 7).
6. **`Strip(string)`**: removes any `\x1b[…m` sequences from a
   string. Useful for length / alignment math when the caller has
   to mix coloured + plain text and align columns.
7. **No background colours, no 256-color or RGB**: 8/16-color
   palette covers ~95% of script use cases; extending later if
   demand surfaces is non-breaking.
8. **Tests** under `src/libraries/z42.io/tests/ansi_color.z42`:
   wrapping, TTY-aware enable/disable, manual override, NO_COLOR
   env, Strip, nested compositions.
9. **Doc**: brief section in z42.io README + `Std.IO.Ansi` mention in
   `docs/design/stdlib/organization.md` if it lists per-class API
   surfaces (will check).

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.io/src/Ansi.z42` | NEW | `Std.IO.Ansi` class + colour / style helpers + auto-detect + Strip |
| `src/libraries/z42.io/tests/ansi_color.z42` | NEW | wrapping / disable-when-not-tty / NO_COLOR / Strip / nested compose / SetEnabled override |
| `src/libraries/z42.io/README.md` | MODIFY | mention `Ansi.Red("error")` next to `Console` in the per-class overview |
| `docs/spec/changes/add-console-ansi-color/` | NEW | this spec dir |

**只读引用**：

- `src/libraries/z42.io/src/Console.z42` — `IsTerminal()` API shape
- `src/libraries/z42.io/src/Environment.z42` — `GetEnvironmentVariable` for NO_COLOR
- `scripts/test-vm.z42` / `scripts/regen-golden-tests.z42` — example
  consumers (callers won't be migrated in this spec; they get to
  opt-in to colour later)

## Out of Scope

- **Background colours** (`\x1b[4Xm` codes) — easy follow-up if asked.
- **256-color / RGB true color** — same.
- **Terminal-capability detection beyond TTY + NO_COLOR**
  (`terminfo` / `tput`) — over-engineered for v0; users on an
  ANSI-incapable terminal can `export NO_COLOR=1`.
- **Auto-colourising `Console.WriteLine`** — no implicit magic;
  colours are opt-in via explicit `Ansi.Red(...)` wrappers.
- **Updating existing scripts to use colours** — separate follow-up;
  this spec only ships the API.
- **Windows legacy console (cmd.exe) compatibility** — Win10+ honours
  ANSI escapes by default; older windows users get garbled output if
  they're on Win7 with raw conhost. Acceptable for pre-1.0.

## Open Questions

- [ ] **Class location**: `Std.IO.Ansi` vs `Std.IO.AnsiColor` —
  going with `Ansi` (shorter at call site, covers styles too not just
  colour). Confirm.
- [ ] **`CLICOLOR` / `CLICOLOR_FORCE`** support: BSD-world equivalents
  of `NO_COLOR`. Skipping for v0 — `NO_COLOR` has the most consensus.
  Add if asked.
