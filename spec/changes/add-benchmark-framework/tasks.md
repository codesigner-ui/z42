# Tasks: Add Benchmark Framework

> 状态：🔵 DRAFT（未实施） | 创建：2026-04-29
> 依赖 P0 (add-just-and-ci) 完成。本文件锁定接口契约。

## 进度概览

- [ ] 阶段 1: Rust 微基准 (criterion)
- [ ] 阶段 2: C# 编译器基准 (BDN)
- [ ] 阶段 3: z42 端到端基准 (hyperfine)
- [ ] 阶段 4: Baseline schema + diff 工具
- [ ] 阶段 5: just 接入 + CI 接入
- [ ] 阶段 6: 文档同步
- [ ] 阶段 7: 验证

---

## 阶段 1: Rust 微基准

- [ ] 1.1 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[dev-dependencies]` 加 `criterion = "0.5"`
- [ ] 1.2 `[[bench]]` 段注册 3 个 bench：`interp_bench`、`gc_bench`、`decoder_bench`
- [ ] 1.3 [src/runtime/benches/interp_bench.rs](src/runtime/benches/interp_bench.rs) 实现 3 个 benchmark：arith_loop、call_overhead、dispatch
- [ ] 1.4 [src/runtime/benches/gc_bench.rs](src/runtime/benches/gc_bench.rs) 实现 3 个 benchmark：alloc_small、alloc_large、collect_cycle
- [ ] 1.5 [src/runtime/benches/decoder_bench.rs](src/runtime/benches/decoder_bench.rs) 实现 zbc 解码吞吐
- [ ] 1.6 [src/runtime/benches/README.md](src/runtime/benches/README.md) 目录 README

## 阶段 2: C# 编译器基准

- [ ] 2.1 [src/compiler/z42.Bench/z42.Bench.csproj](src/compiler/z42.Bench/z42.Bench.csproj) BDN 项目（OutputType=Exe，Release）
- [ ] 2.2 [src/compiler/z42.Bench/Program.cs](src/compiler/z42.Bench/Program.cs) BDN 入口
- [ ] 2.3 [src/compiler/z42.Bench/CompileBenchmarks.cs](src/compiler/z42.Bench/CompileBenchmarks.cs) Lex / Parse / TypeCheck / Codegen 四阶段
- [ ] 2.4 [src/compiler/z42.Bench/Inputs/](src/compiler/z42.Bench/Inputs/) 三档输入 small (50行) / medium (500行) / large (5000行)
- [ ] 2.5 [src/compiler/z42.slnx](src/compiler/z42.slnx) 加入 z42.Bench 项目引用

## 阶段 3: z42 端到端基准

- [ ] 3.1 [bench/scenarios/01_fibonacci.z42](bench/scenarios/01_fibonacci.z42) 经典递归
- [ ] 3.2 [bench/scenarios/02_collection_iter.z42](bench/scenarios/02_collection_iter.z42) LinkedList 遍历
- [ ] 3.3 [bench/scenarios/03_string_concat.z42](bench/scenarios/03_string_concat.z42) 字符串拼接
- [ ] 3.4 [bench/scenarios/04_math_loop.z42](bench/scenarios/04_math_loop.z42) 数值循环
- [ ] 3.5 [bench/scenarios/05_hello_startup.z42](bench/scenarios/05_hello_startup.z42) 启动时间
- [ ] 3.6 [bench/scenarios/README.md](bench/scenarios/README.md) 目录 README
- [ ] 3.7 [scripts/bench-run.sh](scripts/bench-run.sh) hyperfine 调度脚本
- [ ] 3.8 `scripts/_merge-bench-results.py` 把 hyperfine JSON 合并为 baseline 格式

## 阶段 4: Baseline schema + diff 工具

- [ ] 4.1 [bench/baseline-schema.json](bench/baseline-schema.json) JSON Schema (Draft 2020-12)
- [ ] 4.2 [bench/baselines/.gitkeep](bench/baselines/.gitkeep) 目录占位
- [ ] 4.3 [.gitignore](.gitignore) 加 `bench/baselines/*.json`、`bench/results/`、保留 `!.gitkeep`
- [ ] 4.4 [scripts/bench-diff.sh](scripts/bench-diff.sh) diff 工具，按 design.md Decision 3 接口
- [ ] 4.5 `scripts/bench-fetch-baseline.sh` 从 gh-pages / release 拉取 baseline

## 阶段 5: just + CI 接入

- [ ] 5.1 [justfile](justfile) 替换 P0 的 `bench` 占位为完整 6 个 task（design.md Decision 4）
- [ ] 5.2 [.github/workflows/ci.yml](.github/workflows/ci.yml) PR 阶段加 `just bench-quick && just bench-diff main`
- [ ] 5.3 [.github/workflows/bench-update.yml](.github/workflows/bench-update.yml) push to main 时全量 + 上传 baseline
- [ ] 5.4 CI 安装 hyperfine 步骤（macOS: brew；linux: dpkg）

## 阶段 6: 文档同步

- [ ] 6.1 [docs/design/benchmark.md](docs/design/benchmark.md) 完整文档
- [ ] 6.2 [docs/dev.md](docs/dev.md) 加 "Benchmark" 段
- [ ] 6.3 [docs/roadmap.md](docs/roadmap.md) Pipeline 进度表加 P1 完成

## 阶段 7: 验证

- [ ] 7.1 `cargo bench --bench interp_bench` 本地输出 criterion 报告
- [ ] 7.2 `dotnet run --project src/compiler/z42.Bench -c Release` 本地输出 BDN 表格
- [ ] 7.3 `./scripts/bench-run.sh` 输出符合 schema 的 JSON（用 ajv 校验通过）
- [ ] 7.4 `./scripts/bench-diff.sh` 对相同 baseline diff exit 0；对人工注入 10% 退化的 baseline exit 1
- [ ] 7.5 `just bench-quick` 总耗时 ≤ 60 秒
- [ ] 7.6 `just bench` 全量 ≤ 10 分钟
- [ ] 7.7 CI PR 上 quick + diff 步骤 < 90 秒
- [ ] 7.8 push to main 后 baseline 自动更新

## 备注

### 实施依赖

- 必须先完成 [add-just-and-ci](spec/changes/add-just-and-ci/) (P0)
- 安装 hyperfine（开发者本地）

### 风险

- **风险 1**：CI 上 noisy neighbor 导致 bench 抖动 → 用连续 3 次 run 取均值
- **风险 2**：BDN 启动慢（首次 5–10s） → 接受；CI quick 不跑 BDN
- **风险 3**：baseline 跨 commit drift → bench-diff 报告每个 bench 的 baseline commit hash

### 工作量估计

1–2 天（含 schema 设计 + CI 调试）。
