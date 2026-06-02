# Tasks: rename `__bench_now_ns` → `__time_now_mono_ns`

> 状态：🟢 已完成 | 创建：2026-06-03 | 归档：2026-06-03 | 类型：refactor (minimal mode)

**变更说明：** Rename the monotonic-clock builtin from
`__bench_now_ns` to `__time_now_mono_ns`. Same semantics, same
implementation — just moves the builtin name out of the benchmarking
namespace and into the time subsystem where it semantically belongs.

**原因：** `time-rename-bench-now-ns` deferred item from
`add-z42-time` Decision 2 (2026-05-14). Blocked on `add-std-process`
归档 completion (✅ archived 2026-05-14). z42.time / z42.test both
already consume the builtin; the `__bench_now_ns` name is a
historical artifact and currently misleads readers of `Stopwatch.z42`
and `Bencher.z42` (Stopwatch isn't a bench tool; Bencher reaching
into "bench" prefix is incidental). Aligning the name to
`__time_now_mono_ns` makes the dependency direction explicit:
`z42.time` owns the clock primitive; `z42.test/Bencher` consumes it.

**文档影响：**
- `src/libraries/z42.time/README.md` — 3 mentions of the builtin name
- `src/libraries/z42.time/src/Stopwatch.z42` — comment + `[Native]` attribute
- `src/libraries/z42.test/src/Bencher.z42` — comment + `[Native]` attribute + 2 call sites

## Tasks
- [x] 1.1 Rust function rename
- [x] 1.2 Rust registration update
- [x] 1.3 Rust 4 test-call-sites update
- [x] 1.4 Bencher.z42 `[Native]` + method-name + 2 call sites
- [x] 1.5 Stopwatch.z42 same shape
- [x] 1.6 z42.time README + time.md + organization.md + testing docs
- [x] 1.7 `./scripts/test-all.sh` — 6/6 stages GREEN, 260 stdlib files
- [x] 1.8 Moved to `docs/spec/archive/2026-06-03-rename-bench-now-ns-to-time-mono/`
- [x] 1.9 Commit + push

## 备注

Pre-1.0 no-compat policy — old `.zbc` files referencing
`__bench_now_ns` are unreadable after this lands. `test-stdlib.sh`
recompiles every stdlib source, regenerating `.zbc` automatically.
