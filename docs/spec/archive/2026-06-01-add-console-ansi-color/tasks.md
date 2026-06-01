# Tasks: add Std.IO.Ansi color helpers

> 状态：🟢 已完成 | 创建：2026-06-01 | 归档：2026-06-01 | 类型：stdlib feat

## 进度概览
- [x] 阶段 1: `Std.IO.Ansi` class + auto-detect + wrappers
- [x] 阶段 2: Strip helper
- [x] 阶段 3: Tests (13 covering all scenarios)
- [x] 阶段 4: Doc sync (README) + verify + archive

## 阶段 1: Class + wrappers
- [ ] 1.1 Create `src/libraries/z42.io/src/Ansi.z42`
- [ ] 1.2 Module-private state: `bool _enabledChecked` + `bool _enabled`
      + `bool _enabledManual` (manual-override flag)
- [ ] 1.3 `Enabled()` getter: lazy first-call check —
      `Console.IsTerminal()` AND `NO_COLOR` env var is null/empty
- [ ] 1.4 `SetEnabled(bool)` — sets `_enabled` + `_enabledManual=true`
      (subsequent `Enabled()` skips auto-detect)
- [ ] 1.5 Foreground methods Black/Red/Green/Yellow/Blue/Magenta/
      Cyan/White (codes 30–37)
- [ ] 1.6 Bright foreground BrightBlack/…/BrightWhite (codes 90–97)
- [ ] 1.7 Styles Bold/Dim/Italic/Underline/Reverse (codes 1/2/3/4/7)
- [ ] 1.8 Each wrapper: `Enabled() ? wrap(code, s) : s`

## 阶段 2: Strip helper
- [ ] 2.1 `Strip(string s) → string` — scan for `\x1b[`, advance
      until a byte in `0x40..0x7E` range (CSI final byte), drop the
      whole sequence. Concatenate non-CSI runs into a new string via
      StringBuilder.

## 阶段 3: Tests
- [ ] 3.1 Create `src/libraries/z42.io/tests/ansi_color.z42`
- [ ] 3.2 `test_red_wraps_when_enabled` — SetEnabled(true) + Red
- [ ] 3.3 `test_bright_green_uses_90_offset`
- [ ] 3.4 `test_bold_style_emits_code_1`
- [ ] 3.5 `test_nested_compose_red_bold` — visual round-trip
- [ ] 3.6 `test_disabled_passes_through_unchanged`
- [ ] 3.7 `test_disabled_applies_to_every_method` — Black through
      Reverse, asserting input == output
- [ ] 3.8 `test_no_color_env_disables_auto_detect` — set NO_COLOR
      then test wrapper passes through (manual reset before / after
      to avoid leaking)
- [ ] 3.9 `test_manual_set_enabled_overrides_no_color`
- [ ] 3.10 `test_strip_simple_color_wrap`
- [ ] 3.11 `test_strip_nested_wraps`
- [ ] 3.12 `test_strip_plain_text_is_identity`
- [ ] 3.13 `test_strip_handles_non_sgr_escapes` — `\x1b[2J` etc.

## 阶段 4: Doc sync + verify + archive
- [x] 4.1 `src/libraries/z42.io/README.md`: added `Ansi.z42` row to
      the per-class overview table
- [x] 4.2 `./scripts/test-stdlib.sh z42.io` — 43/43 file(s) green
      (incl. 13/13 new ansi_color tests)
- [x] 4.3 `./scripts/test-all.sh` — 6/6 stages GREEN, 257 stdlib
      test files in 22 libs
- [x] 4.4 Moved to `docs/spec/archive/2026-06-01-add-console-ansi-color/`
- [x] 4.5 Commit + push (per workflow auto-archive)

## 备注

- No new VM builtin / IR / language change. Pure-script wrapper on top
  of `Console.IsTerminal()` + `Environment.GetEnvironmentVariable`.
- No-Color env-var semantics: any non-null and non-empty value
  disables. Matches no-color.org convention.
- Caching: `Enabled()` evaluates the auto-detect once and caches.
  TTY status during a single z42 run doesn't change, so caching is
  safe; `SetEnabled` is the escape hatch if a process redirects its
  own stdout mid-run.
