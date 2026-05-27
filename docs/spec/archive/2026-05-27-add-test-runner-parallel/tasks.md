# Tasks: z42-test-runner parallel [Test] execution (--jobs N)

> 状态：🟢 已完成 | 创建：2026-05-27 | 归档：2026-05-27

**变更说明：** 给 `z42-test-runner` 加 `--jobs N` flag。当 `N > 1` 时启用并行子进程执行 — 多个 worker 线程同时 fork `z42vm` 跑各自的 [Test]，结果在主线程聚合后按原顺序输出。串行 stdlib 套件（4 lib × ~30 tests，单 test 50–200ms 冷启动）从 ~30s 缩到 ~5s（8-way）。

**类型：** 最小化 fork/test refactor。不动 in-process runner 路径；只扩展 legacy_subprocess + 加 jobs 控制。

## 设计决策

1. **并行模式 = 强制 subprocess** — 不动 `runner.rs` in-process 路径。原因：`VmContext` 是 `!Send`（`Rc<RefCell>` 模型），无法跨线程共享。subprocess fork 给到本地隔离。
2. **`--jobs N` 默认值** — `available_parallelism()` 或显式 N（≥1）。`N=1` = 现有串行行为，不引入回归。
3. **强制 subprocess 的副作用** — 并行模式无 [Setup]/[Teardown]（subprocess path 本来就没）。在 `--help` 和模式切换时打印 warning。
4. **输出顺序保留** — 收集 `Vec<Option<TestResult>>` 按原 test 顺序填，全部完成后 emit。Pretty / TAP / JSON 三种 formatter 都已是 batched，不需要改。
5. **不引入新 dep** — 用 `std::thread::scope` (Rust 1.63+) + `AtomicUsize` work-stealing counter。比 rayon 轻。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/test-runner/src/main.rs` | MODIFY | 加 `--jobs N` CLI；`N>1` 时分发到新 `parallel::run_tests` |
| `src/toolchain/test-runner/src/parallel.rs` | NEW | `std::thread::scope` worker pool，调既有 `exec::run_one` |
| `scripts/test-lib.sh` | MODIFY | 加 `--jobs N` pass-through；默认值 sensible |
| `src/libraries/z42.test/README.md` | MODIFY | 文档新 flag + 限制说明 |

**只读引用：**
- `src/toolchain/test-runner/src/exec.rs::run_one` — fork-per-test function reused as-is
- `src/toolchain/test-runner/src/main.rs::run` legacy_subprocess branch — pattern reference

## Tasks

- [x] 1.1 NEW `parallel.rs`：
  - 接受 `&[DiscoveredTest]`, `z42vm: &Path`, `zbc_path: &str`, `jobs: usize`
  - `Vec<Option<TestResult>>` 长度 = tests.len()
  - `AtomicUsize` 全局 counter
  - `std::thread::scope` 启动 `min(jobs, tests.len())` workers
  - 每 worker 循环：`fetch_add(1)` → 拿 idx，若 ≥ len break，否则跑 `exec::run_one`，写 `results[idx]` (需要 `Mutex<Vec<Option<_>>>` 或 unsafe — 用 Mutex 更稳)
  - 全部 worker 完成后 `results.into_iter().map(|o| o.unwrap()).collect()`
- [x] 1.2 `main.rs` 加 `--jobs N` CLI arg（default = `None`，runtime resolve）：
  - resolve_jobs(): `None` → `available_parallelism().unwrap_or(1)`; `Some(n)` → max(n, 1)
  - `N == 1` 走现有路径不变
  - `N > 1`：强制 `legacy_subprocess = true` + 调 `parallel::run_tests`
  - 若用户同时给 `--jobs N>1` 和 in-process（默认），打印 stderr warning "parallel mode forces subprocess execution; Setup/Teardown will not run"
- [x] 1.3 `scripts/test-lib.sh`：加 `--jobs` arg + 透传到 z42-test-runner（默认透传 `None` = 让 runner 自己 detect）
- [x] 1.4 写 1 个 e2e smoke：跑 z42.crypto（~20 tests）serial vs --jobs 8 对比 wall-clock，断言 parallel < serial / 2
- [x] 1.5 更新 z42.test/README 加 flag + 限制
- [x] 1.6 归档 + commit + push

## 备注

- 不并行 in-process 是显式 trade-off — VmContext non-Send 是 design constraint，绕开它会复杂得多
- 并行无 Setup/Teardown 是已知限制；接受 trade-off，因为 stdlib 测试几乎不用它们
- 真正的"并行 in-process" 等 z42 GC v2 + 真线程模型才能做，长期 backlog
