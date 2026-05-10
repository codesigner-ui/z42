# Spec: Benchmark Framework

## ADDED Requirements

### Requirement: 三层 benchmark 工具

#### Scenario: Rust 微基准运行

- **WHEN** 执行 `cargo bench --manifest-path src/runtime/Cargo.toml --bench interp_bench`
- **THEN** criterion 输出每个 benchmark 的中位数 + 95% 置信区间
- **AND** HTML 报告生成在 `src/runtime/target/criterion/`

#### Scenario: C# 编译器基准运行

- **WHEN** 执行 `dotnet run --project src/compiler/z42.Bench -c Release`
- **THEN** BenchmarkDotNet 输出每个 benchmark 的均值 / 标准差 / 内存分配
- **AND** 至少包含 Lex / Parse / TypeCheck / Codegen 四个阶段

#### Scenario: z42 端到端基准运行

- **WHEN** 执行 `./scripts/bench-run.sh`
- **THEN** [bench/scenarios/](bench/scenarios/) 下每个 .z42 用 hyperfine 测 10 次取均值
- **AND** 输出符合 [bench/baseline-schema.json](bench/baseline-schema.json) 的 JSON 到 `bench/results/`

### Requirement: just 入口接入

#### Scenario: 全量 bench

- **WHEN** 执行 `just bench`
- **THEN** 依次运行三层 benchmark；总耗时 ≤ 10 分钟（CI 标准 runner）

#### Scenario: 快速子集

- **WHEN** 执行 `just bench-quick`
- **THEN** 运行 interp_bench 子集 + 2 个端到端场景；总耗时 ≤ 60 秒

#### Scenario: 与 baseline diff

- **WHEN** 执行 `just bench-diff main`
- **THEN** 拉取 main baseline，与当前 `bench/results/` 对比
- **AND** 输出每个 benchmark 的差异百分比

### Requirement: Baseline JSON Schema

#### Scenario: 写入 baseline

- **WHEN** `./scripts/bench-run.sh` 完成
- **THEN** 写入的 JSON 通过 `bench/baseline-schema.json` 校验
- **AND** 含字段：`schema_version=1`、`commit`、`branch`、`os`、`timestamp`、`benchmarks[]`

#### Scenario: 区分 OS

- **WHEN** 在 linux-x64 与 macos-aarch64 各跑一次 bench
- **THEN** 产出两个独立 baseline：`baselines/main-linux-x64.json` 和 `baselines/main-macos-aarch64.json`
- **AND** diff 工具不跨 OS 比较

### Requirement: 退化检测与 PR 门禁

#### Scenario: 无退化时通过

- **WHEN** 当前 bench 与 baseline 对比，所有时间指标差异 ≤ 5%
- **THEN** `bench-diff.sh` exit 0
- **AND** 输出标注 ↑ 表示提升、≈ 表示持平

#### Scenario: 时间退化超阈值时失败

- **WHEN** 任一 benchmark 时间退化 > 5%
- **THEN** `bench-diff.sh` exit 1
- **AND** 输出列出具体 benchmark 名 + 退化百分比

#### Scenario: 内存退化阈值更宽松

- **WHEN** benchmark 内存退化 5%–10%
- **THEN** `bench-diff.sh` 输出警告但 exit 0
- **AND** 内存退化 > 10% 时 exit 1

#### Scenario: PR CI 自动跑 quick

- **WHEN** GitHub PR 触发 CI
- **THEN** linux + macos runner 各跑一次 `just bench-quick && just bench-diff main`
- **AND** 任一退化超阈值时该 runner job 失败

### Requirement: 主分支 baseline 自动更新

#### Scenario: push to main 更新 baseline

- **WHEN** push 或 merge 到 main
- **THEN** `.github/workflows/bench-update.yml` 触发
- **AND** 全量 bench 完成后上传新 baseline 到 gh-pages 或 release artifacts

### Requirement: 文档同步

#### Scenario: benchmark.md 完整描述维护流程

- **WHEN** 阅读 [docs/design/benchmark.md](docs/design/benchmark.md)
- **THEN** 含章节：工具选型 / 三层职责 / baseline schema / 退化阈值 / 新增 bench 步骤 / CI 接入

#### Scenario: dev.md 含 bench 命令

- **WHEN** 阅读 [docs/dev.md](docs/dev.md)
- **THEN** 存在 "Benchmark" 段，列出 `just bench` / `just bench-quick` / `just bench-diff main`
