# Tasks: capture benchmark stats into TestResult JSON

> 状态：🟢 已完成 | 完成：2026-05-31 | 创建：2026-05-31 | 类型：fix (test-runner + Bencher format coordination)

**变更说明：** Spec #6 (add-benchmark-runner-dispatch) ships runner-side
`[Benchmark]` dispatch; users write `var b = new Bencher(); b.iter(...);
b.printSummary("label");` inside the body. The stats land in stdout as
`bench[label] min=X median=Y max=Z samples=N` — visible in CI logs but
**not in the runner's JSON output**, so CI tooling can't programmatically
detect performance regressions without grepping captured logs.

Close the gap by parsing the existing `printSummary` line out of the
subprocess's captured stdout and surfacing the structured stats as a new
optional `bench_stats: Option<BenchStats>` field on `TestResult`. JSON
consumers (perf-tracking dashboards, regression diff tools) get typed
data instead of free-form text.

Scope is intentionally narrow — no Bencher.z42 changes (existing format is
reused), no compiler changes. Only the subprocess path benefits in v1
because in-process runner doesn't capture stdout today (writing through a
captured-stdout shim is bigger work; deferred).

**原因：** observed gap when reviewing benchmark support coverage post-spec-#6;
parsing the existing format is the minimum-cost way to deliver structured
stats without touching Bencher or runtime.

**文档影响：** `docs/design/testing/testing.md` Bencher section gains a
"Stats in JSON output" subsection with the parsed `bench_stats` schema +
the parser format invariant + the in-process Deferred note.

## 任务

- [x] 1.1 `src/toolchain/test-runner/src/result.rs`:
  - 加 `pub struct BenchStats { pub label: String, pub min_ns: i64, pub median_ns: i64, pub max_ns: i64, pub samples: i64, pub total_ns: i64 }` with `#[derive(Debug, Serialize)]`
  - `TestResult` 加 `#[serde(skip_serializing_if = "Option::is_none")] pub bench_stats: Option<BenchStats>` field
  - `TestResult::from_outcome` 不填 bench_stats (Default None); 新 helper `TestResult::with_bench_stats(self, BenchStats) -> Self` for the runner to chain after construction
  - 单元测试: `json_benchmark_includes_bench_stats_when_present`, `json_benchmark_omits_bench_stats_when_absent`
- [x] 1.2 `src/toolchain/test-runner/src/exec.rs`:
  - 加 `extract_bench_stats_from_stdout(stdout: &str, expected_name: &str) -> Option<BenchStats>` parser:
    - 扫每一行 `bench[<label>] min=<n>ns median=<n>ns max=<n>ns samples=<n>`
    - 容忍 label 不等于 expected_name (用户可能 printSummary 不同 label) — 选最后一行 `bench[...]` 作 canonical (最近 sample)
    - 字段顺序固定 (与 Bencher.z42:82-84 一致); 其他顺序不识别 (v1)
    - 单元测试: 4 case (canonical / 无 bench 行 / 多 bench 行取 last / 字段缺失返回 None)
  - `run_one`: subprocess passing path (status.success branch) → 调 parser, 若 Some 用 `with_bench_stats` 注入
  - 类似 failure-path: 若 Test 失败但 stdout 含 bench 行 (rare but possible), 也注入
- [x] 1.3 `src/toolchain/test-runner/src/main.rs` + `parallel.rs`:
  - subprocess paths 的 `TestResult::from_outcome(...)` 调用后 chain `.with_bench_stats(stats)` 当 `outcome` 已捕获 stdout 含 bench 行
  - 把 bench-stats extraction 集成在 `exec::run_one` 内部 (返回 Outcome + Option<BenchStats>) 比 caller-side chain 更对称; 实际选: 让 `exec::run_one` 内部直接 emit Outcome 但需要 caller chain. 暂用 caller-chain (less signature churn)
  - 实际更优: `exec::run_one` 返回 `(Outcome, Option<BenchStats>)`; caller `TestResult::from_outcome_with_stats(...)`. 选这个; 单点改动 cleaner
- [x] 1.4 `src/libraries/z42.test/src/Bencher.z42` doc-comment: 加注 "`printSummary` 的输出格式被 runner 解析为 JSON `bench_stats` 字段; 字段名/顺序不要随手改"
- [x] 1.5 E2E demo: `bench_demo.z42` 已有 `b.printSummary("addition_demo")`; 添加 JSON 输出断言 (scripts/test-stdlib.sh 用 `--format json` 时验证 `bench_stats` 字段)
  - 实际: e2e 验证靠 spot-check, 不加 dedicated test (runner-side unit 覆盖 parser; e2e 覆盖 wiring)
- [x] 1.6 `docs/design/testing/testing.md`:
  - Bencher section 加 "Stats in JSON output" 子段: 展示 `bench_stats` JSON schema, parser invariant (format must match Bencher.printSummary), in-process Deferred note
  - Deferred 表加 `bench-stats-in-process-capture` 条目 (in-process 路径需要 stdout 重定向, 独立 spec)
- [x] 1.7 `cargo build` + `cargo test` GREEN
- [x] 1.8 Manual e2e spot-check: 跑 bench_demo.zbc with `--format json` 看 `bench_stats` 字段
- [x] 1.9 `./scripts/test-all.sh --parallel --jobs=4` 全绿
- [x] 1.10 commit + push + archive

## 备注

- Bencher.printSummary 当前格式 (Bencher.z42:82-84):
  `bench[{label}] min={MinNs}ns median={MedianNs}ns max={MaxNs}ns samples={Samples}`
  Parser 严格匹配这个 shape; 改 Bencher 输出格式必须同时升级 parser (单元测试会
  catch)
- TotalNs 不在 printSummary 输出 → bench_stats.total_ns 在 parser path 填 0 (degraded);
  future Bencher format upgrade 可加 `total=Nns` 后 parser 自动捕获
- In-process 路径 不捕获 stdout, 所以 bench_stats 字段始终 None; runner 在
  in-process 模式下打 `note: bench_stats only available in subprocess mode` 当
  --format json AND is_benchmark — defer 这个 warning, 文档说明即可
