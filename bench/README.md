# z42 Benchmarks

## 职责

跨编译器 / 运行时的性能基准基础设施。三层度量：

| 层 | 工具 | 位置 | 状态 |
|----|------|------|------|
| Rust 微基准 | criterion | `src/runtime/benches/` | ✅ P1.A |
| C# 编译器吞吐 | BenchmarkDotNet | `src/compiler/z42.Bench/` | ✅ P1.B |
| z42 端到端 | hyperfine + 自建 harness | `bench/scenarios/` + `scripts/bench-run.sh` | ✅ P1.C |
| 基线对比 | `scripts/bench-diff.sh` | `bench/baselines/` | ✅ P1.D.1 |
| CI bench smoke (artifact) | `.github/workflows/ci.yml` (`bench-e2e` job) | — | ✅ P1.D.2 |
| 主分支 baseline 持久化 | `.github/workflows/bench-update.yml` → `bench-baselines` 分支 | — | ✅ P1.D.3 |
| PR auto-diff (informational) | ci.yml fetch + bench-diff | — | ⏳ P1.D.4 |

## 目录结构

```
bench/
├── README.md                  # 本文件
├── baseline-schema.json       # JSON Schema (Draft 2020-12) for results
├── scenarios/                 # 端到端场景 (.z42 → .zbc → 测时)
│   ├── 01_fibonacci.z42       # 递归 (~ms 量级)
│   ├── 02_math_loop.z42       # 整数循环 (~ms)
│   └── 03_startup.z42         # 最小启动 baseline
├── baselines/                 # main 分支的历史基线（gitignored，CI 上传到 gh-pages）
│   └── .gitkeep
└── results/                   # 当前 run 输出（gitignored）
    └── .gitkeep
```

## 使用

```bash
# 全跑（criterion + BDN + e2e；约 5-10 min 完成）
just bench-rust              # Rust criterion 微基准
just bench-compiler-all      # C# 编译器 BDN（4 stage × 2 input）
just bench-e2e               # z42 端到端（hyperfine on .zbc）

# 快速 sanity（< 60s）
just bench-e2e --quick       # 只跑 startup + fibonacci，少 iter
```

## CI 集成（PR）

PR 到 main 时，`.github/workflows/ci.yml` 的 `bench-e2e` job 自动跑 `just bench-e2e --quick`（仅 ubuntu），把 `bench/results/e2e.json` 上传为 artifact `bench-e2e-results-Linux`。**当前不做自动 diff/门禁** —— 因为：

- CI runner 噪声大（共享 VM），5% 阈值会大量假阳性
- 还没有持久化的 main baseline 可对比

P1.D.4 加 PR fetch + 自动 diff 后才有自动门禁；当前 PR 流程：
1. PR 触发 CI → bench-e2e job 跑完 → 在 PR Checks 页面下载 artifact
2. 本地 `cp downloaded.json bench/baselines/main-darwin-arm64.json`
3. 本地 `just bench-diff` 手动检查

**主分支 baseline 持久化（P1.D.3 已上线）**：每次 push 到 main 自动跑全量 e2e 并把结果提交到 `bench-baselines` 分支：

```
bench-baselines/
├── README.md
└── baselines/
    └── e2e-ubuntu-latest.json   # auto-updated by bench-update.yml
```

手动获取最新 main baseline：

```bash
git fetch origin bench-baselines:bench-baselines
git show bench-baselines:baselines/e2e-ubuntu-latest.json > /tmp/main.json
just bench-diff /tmp/main.json
```

## 与 baseline 对比

```bash
# 1. 把当前结果保存为 baseline（首次或重置）
cp bench/results/e2e.json bench/baselines/main-darwin-arm64.json

# 2. 后续跑 bench 后对比
just bench-e2e
just bench-diff                                # 自动选 main-<os>.json
just bench-diff bench/baselines/main-x.json    # 显式 baseline
```

退化判定：
- 时间退化 > 5%（默认阈值，`--threshold-time` 调整）→ `↑` 标注，exit 1
- 内存退化 > 10%（默认阈值，`--threshold-memory` 调整）→ `↑` 标注
- 改进（更快/更小）→ `↓` 标注，但**不**触发失败
- 浮动 ≤ 阈值 → `≈` 标注

输出示例：

```
  01_fibonacci [time]      50.000 ms → 81.007 ms  ↑ +62.0%
  02_math_loop [time]      15.000 ms → 15.128 ms  ≈ +0.9%

❌ 1 regression(s) above threshold (out of 2 benchmarks)
```

## 输出格式

`bench/results/e2e.json` 与未来的 baseline 文件都遵循 [baseline-schema.json](baseline-schema.json)：

```json
{
  "schema_version": 1,
  "commit": "9dde4ec",
  "branch": "main",
  "os": "darwin-arm64",
  "timestamp": "2026-04-29T12:00:00Z",
  "benchmarks": [
    {
      "name": "01_fibonacci",
      "tier": "z42-e2e",
      "metric": "time",
      "value": 32.4,
      "unit": "ms",
      "ci_lower": 31.8,
      "ci_upper": 33.1,
      "samples": 10
    }
  ]
}
```

## 添加新 scenario

1. 在 `bench/scenarios/` 加 `<NN>_<name>.z42`
2. 顶部注释说明 workload 与预期输出
3. 用 `Console.WriteLine` 打印一个稳定结果（便于验证编译器输出未漂移）
4. workload 大小让单次运行时间 ≥ 50ms（避免 hyperfine 抖动）

## 设计约定

- 不在 bench 里 IO 文件 / 网络（避免抖动）
- bench/baselines/ 目录用 .gitkeep 占位；实际 baselines 由 CI 上传到独立位置（P1.D）
- 度量单位统一：时间用 ms（hyperfine 输出 s 后转换），内存用 KB
- diff 阈值默认 5%（时间） / 10%（内存）
