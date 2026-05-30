//! Parallel subprocess test execution.
//!
//! add-test-runner-parallel (2026-05-27): when CLI `--jobs N` (N > 1) is
//! passed, dispatch tests to `min(N, tests.len())` worker threads each of
//! which forks `z42vm` per test via `exec::run_one`. Results are written
//! into a `Mutex<Vec<Option<TestResult>>>` keyed by original test index
//! so the main thread can emit in declaration order.
//!
//! Why subprocess-only: `VmContext` is `!Send` (Rc<RefCell> model), so
//! the in-process runner from `runner.rs` cannot be parallelised by
//! sharing one VM across threads. Each test runs in its own forked
//! process — slow startup (~50–200 ms per fork) is the cost, but linear
//! speedup up to CPU count makes the typical stdlib suite go from
//! ~30 s serial to ~5 s on 8 cores.
//!
//! Limitation: parallel mode forces subprocess execution, which has no
//! [Setup] / [Teardown] hook support (that's in-process only). Callers
//! who rely on Setup/Teardown should stick with `--jobs 1`.

use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;
use std::thread;

use crate::discover::DiscoveredTest;
use crate::exec;
use crate::result::TestResult;
use crate::skip_eval::SkipEnv;

/// Fork-per-test parallel execution. Returns results in original
/// declaration order. `jobs` is clamped to `[1, tests.len()]`.
///
/// `skip_env` is built once on the main thread (so CLI `--platform`
/// override applies uniformly) and passed by reference into every worker;
/// `SkipEnv` is `Send + Sync` because it owns its strings and hashset.
pub fn run_tests(
    z42vm: &PathBuf,
    zbc_path: &str,
    tests: &[DiscoveredTest<'_>],
    jobs: usize,
    skip_env: &SkipEnv,
) -> Vec<TestResult> {
    let n = tests.len();
    if n == 0 {
        return Vec::new();
    }
    let worker_count = jobs.max(1).min(n);

    // Bench-style storage: one slot per test, populated by whichever
    // worker grabs the index. Mutex-around-Vec rather than per-slot
    // SyncCell because the per-write contention is negligible vs the
    // subprocess fork cost.
    let results: Mutex<Vec<Option<TestResult>>> = Mutex::new((0..n).map(|_| None).collect());
    let next_idx = AtomicUsize::new(0);

    thread::scope(|scope| {
        for _ in 0..worker_count {
            scope.spawn(|| {
                loop {
                    let idx = next_idx.fetch_add(1, Ordering::SeqCst);
                    if idx >= n {
                        return;
                    }
                    let test = &tests[idx];
                    let outcome = exec::run_one(z42vm, zbc_path, test, skip_env);
                    let tr = TestResult::from_outcome(
                        test.method_name.to_string(),
                        outcome,
                        test.is_benchmark,
                    );
                    let mut guard = results.lock().expect("results mutex poisoned");
                    guard[idx] = Some(tr);
                }
            });
        }
    });

    // Drain into final Vec in original order. Every slot must be populated
    // because every index gets exactly one worker (atomic counter).
    results
        .into_inner()
        .expect("results mutex poisoned at drain")
        .into_iter()
        .map(|o| o.expect("parallel runner left an unfilled test slot"))
        .collect()
}
