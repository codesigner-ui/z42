# Spec: OS signal handler — z42 call stack capture on hard crash

## ADDED Requirements

### Requirement: Capture z42 call stack on SIGSEGV / SIGABRT / SIGFPE / SIGILL / SIGBUS

z42vm installs a process-wide signal handler at startup (after `install_panic_hook()`)
for 5 fatal signals. The handler writes a diagnostic report and then re-raises
with default disposition for OS-level coredump.

#### Scenario: SIGSEGV writes z42 stack + reraises

- **WHEN** z42vm receives SIGSEGV during script execution
- **THEN** stderr contains exactly one line `[z42vm signal SIGSEGV at ip=0x<hex>]`
  followed by build banner (`z42vm <ver> (<profile>, <os>/<arch>)`) followed by
  `=== z42 call stack (thread tid=<n>, frames=<m>) ===` followed by `m` frame
  lines `  #<idx>  <func_name> at <file>:<line>:<col>`
- **AND** process exits via default SIGSEGV disposition (kernel coredump if `ulimit -c` permits; exit status reports `signal SIGSEGV`)

#### Scenario: SIGABRT same path

- **WHEN** native FFI calls `libc::abort()` raising SIGABRT
- **THEN** same header pattern with `SIGABRT` substituted; same z42 stack capture; same reraise

#### Scenario: SIGFPE / SIGILL / SIGBUS same path

- **WHEN** any of {SIGFPE, SIGILL, SIGBUS} fires
- **THEN** same header + capture + reraise

#### Scenario: Call stack lock contended — graceful degradation

- **WHEN** signal fires while another thread holds `VmCore.vm_contexts` mutex (e.g. GC mark phase mid-iteration)
- **THEN** handler writes `=== z42 call stack: unavailable (lock contended) ===` (single line) instead of stack frames
- **AND** still writes header + banner + reraises — i.e. degradation is partial, not silent

#### Scenario: VmContext call_stack lock contended per-thread

- **WHEN** vm_contexts lock obtained but an individual `VmContext.call_stack` mutex is held by signaled thread itself (recursive lock attempt)
- **THEN** handler writes that one thread's marker as `tid=<n>: <call stack lock contended>` and continues with remaining VmContexts

#### Scenario: Zero VmContexts registered

- **WHEN** signal fires before any VmContext has been created (e.g. during early `--info` startup or library load failure)
- **THEN** handler writes `=== z42 call stack: no VmContext registered ===` and reraises

### Requirement: `Z42_CRASH_DIR` persistence works for OS signals

#### Scenario: Z42_CRASH_DIR set — file pre-opened at install

- **WHEN** `Z42_CRASH_DIR=/var/log/z42` is set in environment and the directory exists/is writable
- **WHEN** any covered signal fires
- **THEN** the same report content is also written to `<dir>/z42vm-crash-<install_ts_ns>.txt`
- **AND** stderr final line is `[panic hook] crash report written to <path>` (matches Phase 1 message format for consistency)

#### Scenario: Z42_CRASH_DIR unset

- **WHEN** `Z42_CRASH_DIR` is unset
- **THEN** report written only to stderr; no file IO from signal handler

#### Scenario: Z42_CRASH_DIR set but unwritable

- **WHEN** `Z42_CRASH_DIR=/no/such/path` at install time
- **THEN** install logs a warning at startup: `tracing::warn!("Z42_CRASH_DIR <path> is not writable; OS signal reports go to stderr only")`
- **AND** when signal fires, report still goes to stderr (degradation OK)

### Requirement: Cross-platform safety — POSIX only

#### Scenario: macOS / Linux install signal handlers

- **WHEN** z42vm starts on `target_family = "unix"`
- **THEN** the 5 handlers are installed; `tracing::debug!("OS signal handlers installed for SIGSEGV/SIGABRT/SIGFPE/SIGILL/SIGBUS")`

#### Scenario: Windows compiles but does not install

- **WHEN** z42vm compiles under `target_family = "windows"`
- **THEN** the `signal_handler` module is `#[cfg(unix)]`-gated so it does not compile on Windows
- **AND** the `main.rs` install call is also `#[cfg(unix)]`-gated; Windows builds succeed silently with no OS signal capture (Phase 2.1 future spec covers VEH)

### Requirement: Phase 1 panic hook still works

#### Scenario: Rust panic + signal handler coexist

- **WHEN** Rust code calls `panic!("kaboom")` (no OS signal involved)
- **THEN** Phase 1 panic hook fires (existing behavior unchanged); OS signal handler is **not** invoked
- **AND** if signal subsequently fires during panic unwinding (e.g. double fault), OS signal handler activates per its own scenario

#### Scenario: install order

- **WHEN** main() runs
- **THEN** `install_panic_hook()` runs first; `install_signal_handlers()` runs second; both installed before any further work

## MODIFIED Requirements

### `Z42_CRASH_DIR` semantics extended

**Before**: env var honored only by Rust panic hook (Phase 1).

**After**: env var also honored by OS signal handler. Both panic + signal write to the same `<dir>/z42vm-crash-<install_ts_ns>.txt` file (file opened once at install time, used by both code paths).

## IR Mapping

n/a — no IR / opcode changes.

## Pipeline Steps

- [ ] Lexer — n/a
- [ ] Parser / AST — n/a
- [ ] TypeChecker — n/a
- [ ] IR Codegen — n/a
- [ ] zbc Writer / Reader — n/a
- [x] VM runtime startup — `install_signal_handlers()` invoked after `install_panic_hook()` in `main()`
- [x] `Cargo.toml` — add `signal-hook-registry` + `libc`
- [x] `signal_handler.rs` (NEW module) — async-signal-safe handler + 5 signal install
- [x] Unit tests for itoa / signal name table
- [x] Integration test: spawn helper binary, raise signal, verify stderr + crash file
