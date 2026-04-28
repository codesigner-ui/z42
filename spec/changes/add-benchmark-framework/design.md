# Design: Benchmark Framework

## Architecture

```
                ┌──────────────────────────────────┐
                │         just bench [args]        │
                └──────────────┬───────────────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        ▼                      ▼                      ▼
   ┌─────────────┐      ┌─────────────┐      ┌──────────────┐
   │  cargo bench │     │ dotnet run  │      │ scripts/     │
   │  (criterion) │     │  -c Release │      │ bench-run.sh │
   │              │     │  (BDN)      │      │ (.z42 e2e)   │
   └──────┬───────┘     └──────┬──────┘      └──────┬───────┘
          │                    │                    │
          └────────────────────┴────────────────────┘
                               │
                               ▼
            ┌──────────────────────────────────────┐
            │  bench/baselines/<branch>-<os>.json  │
            │  schema: bench/baseline-schema.json  │
            └──────────────┬───────────────────────┘
                           │
                           ▼
            ┌──────────────────────────────────────┐
            │  scripts/bench-diff.sh               │
            │  current vs baseline → exit 0 / 非零 │
            └──────────────────────────────────────┘
```

## Decisions

### Decision 1: 三层 benchmark 工具选型

| 层 | 工具 | 理由 |
|----|------|------|
| Rust 微基准 | **criterion** | 事实标准；统计严谨（置信区间）；HTML 报告 |
| C# 编译器吞吐 | **BenchmarkDotNet** | 事实标准；与 dotnet tool 集成 |
| .z42 端到端 | **hyperfine + 自建 harness** | hyperfine 测时严格；harness 输出 JSON |

不选 `cargo bench`（不稳定 API、统计弱）；不选自建 stopwatch（不严谨）。

### Decision 2: baseline JSON Schema（锁定）

[bench/baseline-schema.json](bench/baseline-schema.json)：

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://z42.dev/schemas/bench-baseline-v1.json",
  "title": "z42 Benchmark Baseline v1",
  "type": "object",
  "required": ["schema_version", "commit", "branch", "os", "timestamp", "benchmarks"],
  "properties": {
    "schema_version": { "const": 1 },
    "commit": { "type": "string", "pattern": "^[0-9a-f]{7,40}$" },
    "branch": { "type": "string" },
    "os": { "enum": ["linux-x64", "macos-aarch64", "macos-x64", "windows-x64"] },
    "timestamp": { "type": "string", "format": "date-time" },
    "rustc_version": { "type": "string" },
    "dotnet_version": { "type": "string" },
    "benchmarks": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["name", "tier", "metric", "value", "unit"],
        "properties": {
          "name": { "type": "string" },
          "tier": { "enum": ["rust-micro", "csharp-throughput", "z42-e2e"] },
          "metric": { "enum": ["time", "throughput", "memory"] },
          "value": { "type": "number" },
          "unit": { "enum": ["ns", "us", "ms", "s", "ops/sec", "bytes", "KB", "MB"] },
          "ci_lower": { "type": ["number", "null"] },
          "ci_upper": { "type": ["number", "null"] },
          "samples": { "type": ["integer", "null"] }
        }
      }
    }
  }
}
```

### Decision 3: Diff 阈值与失败规则

- **默认阈值：5%** 时间退化（性能下降 >5% → exit 非零）
- 单点波动可能导致假警报 → diff 工具要求**连续 3 次** run 平均值超过阈值才报失败（PR 上跑 3 次）
- 内存退化阈值：10%（更宽松，因为内存度量噪声大）
- **改进（性能提升）不报失败**，只在输出中标注 ↑

[scripts/bench-diff.sh](scripts/bench-diff.sh) 接口：

```bash
./scripts/bench-diff.sh \
    --current bench/results/current.json \
    --baseline bench/baselines/main-linux.json \
    --threshold-time 0.05 \
    --threshold-memory 0.10
# exit 0: 无退化；exit 1: 有退化；exit 2: 工具错误
```

### Decision 4: just bench 子命令（替换 P0 占位）

```just
# 全部三层
bench: bench-rust bench-compiler bench-e2e

bench-rust:
    cargo bench --manifest-path src/runtime/Cargo.toml --bench interp_bench
    cargo bench --manifest-path src/runtime/Cargo.toml --bench gc_bench
    cargo bench --manifest-path src/runtime/Cargo.toml --bench decoder_bench

bench-compiler:
    dotnet run --project src/compiler/z42.Bench -c Release

bench-e2e:
    ./scripts/bench-run.sh

# 快速子集（CI PR 用，<60s）
bench-quick:
    cargo bench --manifest-path src/runtime/Cargo.toml --bench interp_bench -- --quick
    ./scripts/bench-run.sh --quick

# 与 baseline diff
bench-diff baseline="main":
    ./scripts/bench-diff.sh \
        --current bench/results/current.json \
        --baseline bench/baselines/{{baseline}}-$(uname -s).json
```

### Decision 5: criterion bench 文件结构

每个 bench 文件 ≤ 200 行；放 `src/runtime/benches/`：

```rust
// src/runtime/benches/interp_bench.rs
use criterion::{criterion_group, criterion_main, Criterion, BenchmarkId};
use z42_runtime::interp::Interpreter;

fn bench_arith_loop(c: &mut Criterion) {
    let zbc = include_bytes!("../tests/fixtures/arith_loop.zbc");
    c.bench_function("interp/arith_loop_1k", |b| {
        b.iter(|| {
            let mut vm = Interpreter::new();
            vm.run(zbc).unwrap();
        });
    });
}

fn bench_call_overhead(c: &mut Criterion) { /* ... */ }
fn bench_dispatch(c: &mut Criterion) { /* ... */ }

criterion_group!(benches, bench_arith_loop, bench_call_overhead, bench_dispatch);
criterion_main!(benches);
```

`Cargo.toml`：

```toml
[dev-dependencies]
criterion = { version = "0.5", features = ["html_reports"] }

[[bench]]
name = "interp_bench"
harness = false

[[bench]]
name = "gc_bench"
harness = false

[[bench]]
name = "decoder_bench"
harness = false
```

### Decision 6: BDN 项目结构

```
src/compiler/z42.Bench/
├── z42.Bench.csproj         # OutputType=Exe, Configurations=Release
├── Program.cs               # BenchmarkSwitcher.FromAssembly(...).Run(args)
├── CompileBenchmarks.cs     # [Benchmark] Lex / Parse / TypeCheck / Codegen
└── Inputs/
    ├── small.z42            # ~50 行
    ├── medium.z42           # ~500 行
    └── large.z42            # ~5000 行
```

`CompileBenchmarks.cs`：

```csharp
[MemoryDiagnoser]
public class CompileBenchmarks
{
    [ParamsSource(nameof(InputSources))]
    public string Input { get; set; }
    public static IEnumerable<string> InputSources => ["small", "medium", "large"];

    [Benchmark] public void Lex() => /* ... */;
    [Benchmark] public void Parse() => /* ... */;
    [Benchmark] public void TypeCheck() => /* ... */;
    [Benchmark] public void Codegen() => /* ... */;
}
```

### Decision 7: z42 端到端 harness

[scripts/bench-run.sh](scripts/bench-run.sh)：

```bash
#!/usr/bin/env bash
# 用 hyperfine 测每个 .zbc 的执行时间，输出 JSON
set -euo pipefail

QUICK=${1:-}
SCENARIOS_DIR="bench/scenarios"
RESULTS="bench/results/e2e.json"

mkdir -p bench/results

scenarios=("$SCENARIOS_DIR"/*.z42)
[[ "$QUICK" == "--quick" ]] && scenarios=("${scenarios[@]:0:2}")

for src in "${scenarios[@]}"; do
    name=$(basename "$src" .z42)
    # 编译
    dotnet run --project src/compiler/z42.Driver -- compile "$src" -o "/tmp/${name}.zbc"
    # 测时
    hyperfine --warmup 3 --runs 10 --export-json "/tmp/${name}-bench.json" \
        "z42vm /tmp/${name}.zbc --mode interp"
done

# 合并为 baseline 格式
python scripts/_merge-bench-results.py /tmp/*-bench.json > "$RESULTS"
```

### Decision 8: CI 接入策略

[.github/workflows/ci.yml](.github/workflows/ci.yml) 在 P0 基础上加：

```yaml
- name: Benchmark (quick, PR only)
  if: github.event_name == 'pull_request' && matrix.os != 'windows-latest'
  run: |
    just bench-quick
    just bench-diff main
```

新建 [.github/workflows/bench-update.yml](.github/workflows/bench-update.yml)（仅 push to main 触发）：

```yaml
on:
  push:
    branches: [main]

jobs:
  update-baseline:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: just bench  # 全量
      - name: Upload baseline
        # 上传到 gh-pages 或 release artifacts
```

### Decision 9: bench 文件不计入主 build

- `[[bench]]` + `harness = false` → criterion 自管 main
- `cargo build` 不编译 bench；只有 `cargo bench` 才编译
- BDN 项目通过 csproj `Configurations=Release` 默认 Release，不参与 `dotnet build src/compiler/z42.slnx` 的 Debug 路径
- 已有 `dotnet test` 不会运行 bench

## Implementation Notes

### hyperfine 安装

文档说明：

```bash
brew install hyperfine          # macOS
cargo install hyperfine         # 通用
sudo apt install hyperfine      # Ubuntu
```

CI 上：`brew install hyperfine`（macos）或 `wget https://github.com/sharkdp/hyperfine/.../hyperfine.deb && dpkg -i`（linux）。

### Baseline 存储位置

- 本地：`bench/baselines/<branch>-<os>.json`（gitignored —— 否则每次 PR 都会有 diff）
- CI / 公共：`gh-pages` 分支或 GitHub release artifacts
- 拉取：`scripts/bench-fetch-baseline.sh main` 从公共位置下载

### .gitignore 添加

```
bench/baselines/*.json
bench/results/
!bench/baselines/.gitkeep
```

### Z42 端到端 harness 与 .zbc 缓存

- 每次 bench-run 都重新编译 .z42 → .zbc（避免使用陈旧产物）
- 但允许 hyperfine `--prepare` 在第一次预热时跑一次冷启动测量

## Testing Strategy

本 spec 的"测试"是验证 benchmark 框架本身能运行：

- ✅ `cargo bench --bench interp_bench` 在本地输出 criterion 标准格式
- ✅ `dotnet run --project src/compiler/z42.Bench -c Release` 输出 BDN 标准表格
- ✅ `./scripts/bench-run.sh` 输出符合 schema 的 JSON
- ✅ `bench/baseline-schema.json` 用 ajv 校验示例 baseline 通过
- ✅ `./scripts/bench-diff.sh` 对两个相同 baseline diff 输出 0 退化；对人工注入 10% 退化的 baseline 输出 exit 1
- ✅ CI PR 上 `just bench-quick` < 60 秒
- ✅ `just bench` 全量在 CI 上 < 10 分钟

无新增 cargo test / dotnet test 用例（benchmark 自身不通过单测验证）。
