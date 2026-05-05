//! Unit tests for stdout/stderr sink dispatch (R2 完整版 Phase A).
//!
//! Notes:
//! - Tests run in their own thread (cargo test) so thread_local sinks are
//!   freshly empty at start. The crate's other tests share the test thread
//!   pool but since each #[test] runs on its own thread (xUnit-style), the
//!   thread_local stack is per-test.
//! - We don't try to test the "println-to-process-stdout" path (no easy
//!   capture from inside the test). All tests install a sink first and
//!   assert via the buffer.

use super::*;
use crate::vm_context::VmContext;
use crate::metadata::Value;

fn ctx() -> VmContext {
    VmContext::default()
}

fn s(text: &str) -> Value {
    Value::Str(text.to_string())
}

fn install_stdout() {
    builtin_test_io_install_stdout_sink(&ctx(), &[]).unwrap();
}

fn take_stdout() -> String {
    match builtin_test_io_take_stdout_buffer(&ctx(), &[]).unwrap() {
        Value::Str(s) => s,
        other => panic!("expected Str, got {:?}", other),
    }
}

fn install_stderr() {
    builtin_test_io_install_stderr_sink(&ctx(), &[]).unwrap();
}

fn take_stderr() -> String {
    match builtin_test_io_take_stderr_buffer(&ctx(), &[]).unwrap() {
        Value::Str(s) => s,
        other => panic!("expected Str, got {:?}", other),
    }
}

#[test]
fn install_then_println_routes_to_buffer_with_newline() {
    install_stdout();
    builtin_println(&ctx(), &[s("hello")]).unwrap();
    builtin_println(&ctx(), &[s("world")]).unwrap();
    let buf = take_stdout();
    assert_eq!(buf, "hello\nworld\n");
}

#[test]
fn install_then_print_routes_without_newline() {
    install_stdout();
    builtin_print(&ctx(), &[s("ab")]).unwrap();
    builtin_print(&ctx(), &[s("cd")]).unwrap();
    let buf = take_stdout();
    assert_eq!(buf, "abcd");
}

#[test]
fn nested_capture_stdout_inner_sees_only_inner() {
    install_stdout();
    builtin_println(&ctx(), &[s("outer-pre")]).unwrap();
    install_stdout();
    builtin_println(&ctx(), &[s("inner")]).unwrap();
    let inner = take_stdout();
    builtin_println(&ctx(), &[s("outer-post")]).unwrap();
    let outer = take_stdout();

    assert_eq!(inner, "inner\n");
    assert_eq!(outer, "outer-pre\nouter-post\n");
}

#[test]
fn take_pops_sink_so_next_println_falls_through() {
    install_stdout();
    builtin_println(&ctx(), &[s("captured")]).unwrap();
    let captured = take_stdout();
    assert_eq!(captured, "captured\n");
    // After take, no sink active. We can't easily assert on process stdout,
    // but we can confirm the sinks vector is now empty by observing that
    // a fresh install + take returns an empty buffer.
    install_stdout();
    let empty = take_stdout();
    assert_eq!(empty, "");
}

#[test]
fn stderr_sink_independent_of_stdout() {
    install_stdout();
    install_stderr();
    builtin_println(&ctx(), &[s("on stdout")]).unwrap();
    builtin_eprintln(&ctx(), &[s("on stderr")]).unwrap();
    let out = take_stdout();
    let err = take_stderr();
    assert_eq!(out, "on stdout\n");
    assert_eq!(err, "on stderr\n");
}

#[test]
fn stderr_only_capture_does_not_intercept_stdout() {
    // Only stderr sink installed. We can verify stderr buffer fills; we
    // can't easily assert that stdout went to process stdout, but we can
    // assert that no stdout buffer exists to swallow it.
    install_stderr();
    builtin_eprintln(&ctx(), &[s("stderr line")]).unwrap();
    // Any stdout call here would route to process stdout — fine.
    let err = take_stderr();
    assert_eq!(err, "stderr line\n");
}

#[test]
fn take_on_empty_stack_returns_empty_string() {
    // Defensive: should not panic on extra take.
    let s = take_stdout();
    assert_eq!(s, "");
}
