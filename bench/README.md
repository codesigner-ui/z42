# z42 Benchmarks

## 职责

跨编译器 / 运行时的性能基准基础设施。三层度量：

| 层 | 工具 | 位置 | 状态 |
|----|------|------|------|
| Rust 微基准 | criterion | `src/runtime/benches/` | ✅ P1.A |
| C# 编译器吞吐 | BenchmarkDotNet | `src/compiler/z42.Bench/` | ✅ P1.B |
| z42 端到端 | hyperfine + 自建 harness | `bench/scenarios/` + `scripts/bench-run.sh` | ✅ P1.C |
| 基线对比与门禁 | `scripts/bench-diff.sh` + CI | `bench/baselines/` | ⏳ P1.D |

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
