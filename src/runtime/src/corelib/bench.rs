//! Bench helpers — minimal native primitives for `Std.Test.Bencher`.
//!
//! `__bench_now_ns` returns nanoseconds since the first call (an internal
//! EPOCH); used as a monotonic clock by `Bencher.iter` to time samples.
//!
//! `__bench_black_box` is the identity function. Interp does no
//! dead-code elimination, so the wrapper is structurally a no-op today.
//! It exists so user code can mark "do not optimise this away" the way
//! criterion / std::hint::black_box does — once the JIT learns to elide
//! pure expressions, this hook is the canonical opt-out.

use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;
use std::sync::OnceLock;
use std::time::Instant;

static EPOCH: OnceLock<Instant> = OnceLock::new();

pub fn builtin_bench_now_ns(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let epoch = EPOCH.get_or_init(Instant::now);
    let ns = Instant::now().duration_since(*epoch).as_nanos() as i64;
    Ok(Value::I64(ns))
}

pub fn builtin_bench_black_box(_ctx: &VmContext, args: &[Value]) -> Result<Value> {
    Ok(args.first().cloned().unwrap_or(Value::Null))
}

#[cfg(test)]
#[path = "bench_tests.rs"]
mod bench_tests;
