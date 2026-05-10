# Proposal: Add Benchmark Framework (criterion + BenchmarkDotNet + baselines)

## Why

z42 当前**完全无 benchmark 流程**：性能回归无追踪、无门禁、无对比手段。新写的 GC、JIT、stdlib 实现可能默默退化 10–50% 而无人察觉。

随着 stdlib 扩张（z42.collections 已有 LinkedList，math/text/io 持续扩充）和 GC 重构（MagrGC Phase 1 已落地），缺乏 benchmark 等于在裸奔。

P1 引入三层 benchmark：
1. **Rust 微基准**（criterion）—— 量 VM 内部操作（GC alloc、interp dispatch、JIT codegen）
2. **C# 编译器吞吐**（BenchmarkDotNet）—— 量编译速度（行/秒、ms/file）
3. **z42 端到端**（自建 harness）—— 量真实程序的启动 + 运行时间

并配套 baseline JSON + diff 工具，PR 阶段 >5% 退化阻塞。

## What Changes

- **Rust 微基准**：`src/runtime/benches/` 接入 criterion，初始覆盖 interp dispatch / GC alloc / decoder 三类
- **C# 编译器基准**：新建 `src/compiler/z42.Bench/` 项目，BDN 测 lex/parse/typecheck/codegen 各阶段吞吐
- **z42 端到端**：`bench/scenarios/*.z42` 5 个真实场景；harness 用 `time` + 内存峰值
- **Baseline 机制**：`bench/baselines/<branch>.json`（commit、timestamp、benchmarks 数组）
- **Diff 工具**：`scripts/bench-diff.sh` 比对当前 run 与 baseline JSON，>5% 退化 exit 非零
- **just 接入**：`just bench` / `just bench vm` / `just bench compiler` / `just bench --baseline main` / `just bench --quick`
- **CI 接入**：PR step 加 `just bench --quick` 性能门禁；push to main 加 `just bench && update baseline`
- **文档**：[docs/design/benchmark.md](docs/design/benchmark.md) 新建

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/Cargo.toml` | MODIFY | `[dev-dependencies]` 加 criterion；`[[bench]]` 段注册 3 个 bench |
| `src/runtime/benches/interp_bench.rs` | NEW | interp dispatch / 算术 / 调用开销 |
| `src/runtime/benches/gc_bench.rs` | NEW | GC alloc / collect / barrier 开销 |
| `src/runtime/benches/decoder_bench.rs` | NEW | zbc 解码吞吐 |
| `src/runtime/benches/README.md` | NEW | 目录 README |
| `src/compiler/z42.Bench/z42.Bench.csproj` | NEW | BDN 项目 |
| `src/compiler/z42.Bench/Program.cs` | NEW | BDN 入口 |
| `src/compiler/z42.Bench/CompileBenchmarks.cs` | NEW | 4 阶段基准（lex/parse/typecheck/codegen） |
| `src/compiler/z42.Bench/Inputs/` | NEW | 基准用 .z42 输入（小/中/大三档） |
| `src/compiler/z42.slnx` | MODIFY | 加入 z42.Bench 项目引用 |
| `bench/scenarios/01_fibonacci.z42` | NEW | 经典递归 |
| `bench/scenarios/02_collection_iter.z42` | NEW | LinkedList 遍历 |
| `bench/scenarios/03_string_concat.z42` | NEW | 字符串拼接 |
| `bench/scenarios/04_math_loop.z42` | NEW | 数值循环 |
| `bench/scenarios/05_hello_startup.z42` | NEW | 启动时间度量 |
| `bench/scenarios/README.md` | NEW | 目录 README |
| `bench/baselines/.gitkeep` | NEW | baselines 目录占位 |
| `bench/baseline-schema.json` | NEW | baseline JSON Schema (Draft 2020-12) |
| `scripts/bench-run.sh` | NEW | 运行所有三层 benchmark，输出 baseline JSON |
| `scripts/bench-diff.sh` | NEW | diff 当前与 baseline，>5% exit 非零 |
| `justfile` | MODIFY | 实现 `bench` 系列 task（替换 P0 占位） |
| `.github/workflows/ci.yml` | MODIFY | PR 加 `just bench --quick`；push to main 加 baseline 上传 |
| `.github/workflows/bench-update.yml` | NEW | push to main 时更新 baseline |
| `docs/design/benchmark.md` | NEW | benchmark 设计与维护指南 |
| `docs/dev.md` | MODIFY | 加 "Benchmark" 段 |

**只读引用**：
- [src/runtime/src/interp/](src/runtime/src/interp/) — 理解 interp 入口签名
- [src/runtime/src/gc/](src/runtime/src/gc/) — 理解 GC API
- [src/compiler/z42.Compiler/](src/compiler/z42.Compiler/) — 理解 pipeline 阶段入口
- [justfile](justfile) — P0 已建好，本 spec 修改 bench 部分
- [.github/workflows/ci.yml](.github/workflows/ci.yml) — P0 已建好

## Out of Scope

- **Benchmark 用例的"覆盖率"目标**：本 spec 只搭框架 + 初始 5–10 个 bench；扩充覆盖留给后续日常迭代
- **JIT vs interp 性能对比报告**：框架本身支持，但生成正式对比报告不在本 spec
- **历史趋势可视化**（dashboard / Grafana）：超出范围，单独立项
- **微基准的稳定性保证**（pinning CPU、关闭 turbo）：CI 上不强制；本地由开发者自决
- **Benchmark 失败的 root cause 工具**（pprof / flamegraph 集成）：单独立项

## Open Questions

- [ ] **Q1**：bench --baseline 的对照基线从哪取？
  - 倾向：从 `gh-pages` 分支或 GitHub release artifacts 拉取最新 main baseline
- [ ] **Q2**：5% 退化阈值是否各 bench 单独可配？
  - 倾向：本 spec 全局 5%，后续按 bench tag 单独配置
- [ ] **Q3**：CI 上 macOS / Linux 的 baseline 是否分开维护？
  - 倾向：分开（`baselines/main-linux.json`、`main-macos.json`），不跨平台 diff
- [ ] **Q4**：z42 端到端 harness 用什么测时？
  - 倾向：`hyperfine` 命令行工具（成熟、JSON 输出友好）
