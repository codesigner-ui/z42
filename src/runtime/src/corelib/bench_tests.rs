use super::*;
use crate::metadata::Value;
use crate::vm_context::VmContext;

fn ctx() -> VmContext { VmContext::default() }

#[test]
fn now_ns_is_monotonic_non_decreasing() {
    let a = match builtin_bench_now_ns(&ctx(), &[]).unwrap() {
        Value::I64(n) => n,
        other => panic!("expected I64, got {:?}", other),
    };
    let b = match builtin_bench_now_ns(&ctx(), &[]).unwrap() {
        Value::I64(n) => n,
        other => panic!("expected I64, got {:?}", other),
    };
    assert!(b >= a, "expected monotonic time, but got a={} b={}", a, b);
    // Both should be non-negative since EPOCH initialises to Instant::now()
    // on the first call within this test process.
    assert!(a >= 0);
}

#[test]
fn now_ns_advances_across_busy_loop() {
    let a = match builtin_bench_now_ns(&ctx(), &[]).unwrap() {
        Value::I64(n) => n,
        _ => unreachable!(),
    };
    // Trivial busy work to make sure the second sample is strictly later in
    // wall time even on extremely fast hosts. Don't sleep — flaky on CI.
    let mut sum: u64 = 0;
    for i in 0..100_000u64 { sum = sum.wrapping_add(i); }
    std::hint::black_box(sum);
    let b = match builtin_bench_now_ns(&ctx(), &[]).unwrap() {
        Value::I64(n) => n,
        _ => unreachable!(),
    };
    assert!(b > a, "expected b > a after busy loop, got a={} b={}", a, b);
}

#[test]
fn black_box_returns_arg_unchanged_int() {
    let r = builtin_bench_black_box(&ctx(), &[Value::I64(42)]).unwrap();
    assert_eq!(r, Value::I64(42));
}

#[test]
fn black_box_returns_arg_unchanged_bool() {
    let r = builtin_bench_black_box(&ctx(), &[Value::Bool(true)]).unwrap();
    assert_eq!(r, Value::Bool(true));
}

#[test]
fn black_box_returns_arg_unchanged_string() {
    let r = builtin_bench_black_box(&ctx(), &[Value::Str("xyz".into())]).unwrap();
    assert_eq!(r, Value::Str("xyz".into()));
}

#[test]
fn black_box_no_arg_returns_null() {
    let r = builtin_bench_black_box(&ctx(), &[]).unwrap();
    assert_eq!(r, Value::Null);
}
