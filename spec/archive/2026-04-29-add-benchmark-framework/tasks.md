# Tasks: Add Benchmark Framework

> 状态：🟢 已完成（主体） | 创建：2026-04-29 | 完成：2026-04-29
> 依赖 P0 (add-just-and-ci) 完成。
>
> 实施拆为 6 次提交（P1.A → P1.D.4）。详见底部"实施记录"。

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

---

## 实施记录（2026-04-29）

实施分 6 次提交完成主体，每次独立可回滚：

| Phase | Commit | 范围 |
|-------|--------|------|
| **P1.A** | `edade7b` | criterion dev-dep + `src/runtime/benches/smoke_bench.rs`（2 个纯 Rust 基线 bench） + `just bench-rust` |
| **P1.B** | `16cabef` | `src/compiler/z42.Bench/`（BDN 0.14.0；4 阶段 × 2 输入 = 8 benchmarks）+ `just bench-compiler` + `bench-compiler-all` |
| **P1.C** | `4e62553` | `bench/scenarios/{01_fibonacci,02_math_loop,03_startup}.z42` + `bench/baseline-schema.json` + `scripts/bench-run.sh` + `scripts/_merge-bench-results.py` + `just bench-e2e` |
| **P1.D.1** | `c8c3f5e` | `scripts/bench-diff.sh`（jq + awk diff）+ `just bench-diff` |
| **P1.D.2** | `eb87e9b` | ci.yml `bench-e2e` job（PR 触发，linux）+ artifact upload；顺便修 .NET 9.0.x → 10.0.x |
| **P1.D.3** | `c82d830` | `.github/workflows/bench-update.yml`：push to main 跑全量 + 提交到独立 `bench-baselines` 分支 |
| **P1.D.4** | `9755505` | ci.yml `bench-e2e` 加 fetch baseline + informational diff 到 `$GITHUB_STEP_SUMMARY` |

### 实施过程偏差与决策

1. **拆为 6 个 commit 而非 1 个**：原 spec 暗示一次性实施。实际拆为 P1.A → P1.D.4 让每次提交独立可验证；尤其 P1.D 进一步拆 4 个子 phase（local diff → CI smoke → baseline 持久化 → PR diff）让"CI 状态管理"复杂度可控。
2. **JIT/AOT bench 推迟**：原 spec design.md 提到 `interp_bench.rs` / `gc_bench.rs` / `decoder_bench.rs` 各 3 个真实 benchmark；实际只交付 `smoke_bench.rs`（2 个纯 Rust 基线）。原因：真实 VM bench 需要构造最小 Module / 暴露 Interpreter 公共 API，工作量翻倍且与本期"搭框架"目标偏离。后续可作为独立"bench coverage 扩充"任务。
3. **BDN large.z42 跳过**：原 spec 三档输入（small / medium / large 5000 行）；实际只交付 small (~50 行) + medium (~250 行)。Large 需要程序生成器，非关键路径。
4. **CI 阈值放宽**：原 spec 5% 时间退化阻塞；实际改为 15% (time) / 20% (memory) **informational only**（不阻塞）。CI 共享 VM 噪声大，5% 阈值会大量假阳性。等以后引入 dedicated runner 或多次取均值再调严。
5. **gh-pages → bench-baselines 独立分支**：原 spec 提到"gh-pages 或 release artifacts"；实际选独立分支 `bench-baselines`，避免与未来文档站点冲突且单一职责。
6. **修了 P0 残留 .NET 版本 bug**：CI 设的 9.0.x 但项目 net10.0；P1.D.2 一并修。

### 已知缺口（留 backlog）

- **真实 VM 内部 micro-bench**（interp/gc/decoder） — 推迟到 R 系列后或独立任务
- **BDN large.z42 输入** — 需要程序生成器
- **真正的 PR fail 门禁** — 需要稳定 bench runner 环境
- **多平台 baseline**（linux + macOS） — 当前仅 linux；macOS runner 噪声更大，价值低
- **HTML 报告聚合** — criterion 本身有 HTML 但需 publish；BDN markdown 可解析

这些都是覆盖度与稳定性扩展，不影响当前框架可用性。
