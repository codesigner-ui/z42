# Design: z42-test-runner Compile-Time Discovery

## Architecture

```
                ┌──────────────────────────────────┐
                │  z42-test-runner CLI [paths]     │
                │  (clap derive)                   │
                └──────────────┬───────────────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        ▼                      ▼                      ▼
   ┌──────────┐          ┌──────────┐          ┌──────────┐
   │ discover │          │  runner  │          │  format  │
   │          │          │          │          │ tap/json │
   │  read    │ ────►    │  load    │ ────►    │ /pretty  │
   │  index   │          │  zpkg    │          │          │
   │  filter  │          │  setup   │          └──────────┘
   │          │          │  exec    │
   └──────────┘          │  catch   │
                         │  format  │
                         └─────┬────┘
                               │
                               ▼
                  ┌────────────────────────────┐
                  │  z42_runtime (path-dep)    │
                  │   ↳ load_artifact          │
                  │   ↳ test_index field       │
                  │   ↳ Interpreter::call_func │
                  └────────────────────────────┘
```

## Decisions

### Decision 1: 复用原 P2 CLI 设计

[add-z42-test-runner/design.md Decision 4](../add-z42-test-runner/design.md) 的 CLI 设计**完整保留**。R3 实施时直接采用，无变更。包括：

- positional `[PATHS]`
- `--format <tap|json|pretty>`、`--filter <RE>`
- `--ignored` / `--include-ignored` / `--quiet` / `--verbose` / `--jobs <N>` / `--timeout <SEC>`
- 退出码语义：0/1/2/3

新增 R3 特有：

- `--bench` 切换为 benchmark 模式（默认跑 [Test]，开关后跑 [Benchmark]）
- `--baseline <NAME>` 与 baseline 对比（沿用 P1 baseline JSON 格式）
- `--save-baseline <NAME>` 保存当前结果

### Decision 2: Discovery 流程（新）

```rust
// src/toolchain/test-runner/src/discover.rs
use z42_runtime::metadata::{load_artifact, LoadedArtifact, TestEntry, TestEntryKind};

pub struct DiscoveredTest {
    pub artifact_path: PathBuf,
    pub method_id:     u32,
    pub function_name: String,
    pub kind:          TestEntryKind,
    pub flags:         TestFlags,
    pub setup_methods: Vec<u32>,    // method_ids of [Setup] in same module
    pub teardown_methods: Vec<u32>,
    pub skip_reason:   Option<String>,
    pub expected_throw_type: Option<String>,
    pub test_cases:    Vec<TestCase>,
}

pub fn discover(paths: &[PathBuf], filter: Option<&Regex>) -> Result<Vec<DiscoveredTest>> {
    let mut all = Vec::new();
    for path in paths {
        let zbc_files = collect_zbc_files(path)?;
        for zbc in zbc_files {
            let artifact = load_artifact(zbc.to_str().unwrap())?;
            let setups: Vec<u32> = artifact.test_index.iter()
                .filter(|e| e.kind == TestEntryKind::Setup)
                .map(|e| e.method_id)
                .collect();
            let teardowns: Vec<u32> = artifact.test_index.iter()
                .filter(|e| e.kind == TestEntryKind::Teardown)
                .map(|e| e.method_id)
                .collect();
            for entry in &artifact.test_index {
                if entry.kind == TestEntryKind::Setup || entry.kind == TestEntryKind::Teardown {
                    continue;
                }
                let fn_meta = &artifact.module.functions[entry.method_id as usize];
                if let Some(re) = filter {
                    if !re.is_match(&fn_meta.name) { continue; }
                }
                all.push(DiscoveredTest {
                    artifact_path: zbc.clone(),
                    method_id: entry.method_id,
                    function_name: fn_meta.name.clone(),
                    kind: entry.kind,
                    flags: entry.flags,
                    setup_methods: setups.clone(),
                    teardown_methods: teardowns.clone(),
                    skip_reason: lookup_string(&artifact.module, entry.skip_reason_str_idx),
                    expected_throw_type: lookup_type(&artifact.module, entry.expected_throw_type_idx),
                    test_cases: entry.test_cases.clone(),
                });
            }
        }
    }
    Ok(all)
}
```

### Decision 3: Runner 单测执行流程

```rust
pub fn run_one(test: &DiscoveredTest, opts: &RunOptions) -> TestResult {
    if test.flags.contains(TestFlags::IGNORED) {
        return TestResult::ignored(test);
    }
    if test.flags.contains(TestFlags::SKIPPED) {
        return TestResult::skipped(test, test.skip_reason.clone());
    }

    // 每个 test 一个独立 Interpreter
    let artifact = load_artifact(test.artifact_path.to_str().unwrap()).unwrap();
    let mut ctx = VmContext::new();
    install_lazy_loader(&mut ctx, &artifact);
    let mut interp = build_interpreter(&artifact, &mut ctx);

    // 默认捕获 stdout（参 Rust libtest）
    if !opts.show_output {
        interp.invoke_native("__test_io_install_stdout_sink", &[]).ok();
    }

    let start = Instant::now();
    let outcome = run_with_setup_teardown(&mut interp, test);
    let duration = start.elapsed();

    let captured = if !opts.show_output {
        interp.invoke_native("__test_io_take_stdout_buffer", &[])
            .ok().and_then(|v| v.as_string())
    } else {
        None
    };

    match outcome {
        Outcome::Passed => TestResult::passed(test, duration),
        Outcome::Failed(reason) => TestResult::failed(test, duration, reason, captured),
        Outcome::Skipped(reason) => TestResult::skipped_at_runtime(test, reason),
    }
}

fn run_with_setup_teardown(interp: &mut Interpreter, test: &DiscoveredTest) -> Outcome {
    // 1. Run all [Setup] methods
    for &setup_id in &test.setup_methods {
        if let Err(e) = interp.call_method_id(setup_id, &[]) {
            return Outcome::Failed(format!("setup failed: {e}"));
        }
    }

    // 2. Run test body for each TestCase variant
    let arg_sets = if test.test_cases.is_empty() {
        vec![Vec::new()]
    } else {
        test.test_cases.iter().map(|tc| parse_args(&tc.arg_repr)).collect()
    };

    let mut combined_outcome = Outcome::Passed;
    for args in arg_sets {
        let result = interp.call_method_id(test.method_id, &args);
        let case_outcome = match result {
            Ok(_) => {
                if test.flags.contains(TestFlags::SHOULD_THROW) {
                    Outcome::Failed("expected exception not thrown".into())
                } else {
                    Outcome::Passed
                }
            }
            Err(e) => classify_exception(e, test),
        };
        if !matches!(case_outcome, Outcome::Passed) {
            combined_outcome = case_outcome;
            break; // First failing case stops further variants
        }
    }

    // 3. Run all [Teardown] methods (always, even if test failed)
    for &teardown_id in &test.teardown_methods {
        let _ = interp.call_method_id(teardown_id, &[]);
    }

    combined_outcome
}

fn classify_exception(err: anyhow::Error, test: &DiscoveredTest) -> Outcome {
    let msg = format!("{err}");
    if msg.contains("z42.test.SkipSignal") {
        return Outcome::Skipped(extract_reason(&msg));
    }
    if test.flags.contains(TestFlags::SHOULD_THROW) {
        if let Some(expected) = &test.expected_throw_type {
            if msg.contains(expected) { return Outcome::Passed; }
            return Outcome::Failed(format!("expected exception of type {expected}, got: {msg}"));
        }
    }
    Outcome::Failed(msg)
}
```

### Decision 4: Bencher 模式（criterion-style）

```rust
// src/toolchain/test-runner/src/bencher.rs

pub fn run_benchmark(test: &DiscoveredTest, opts: &BenchOptions) -> BenchResult {
    // 1. Build interp + register Bencher.iter callback
    let mut interp = build_interpreter(...);
    
    // 2. Hand a Bencher instance into the test method.
    // Bencher.iter(closure) stores the closure via __bench_set_iter_closure.
    interp.invoke_native("__bench_install_capture", &[]).ok();
    interp.call_method_id(test.method_id, &[bencher_instance]).ok();
    let closure = interp.invoke_native("__bench_take_iter_closure", &[]).ok();
    let closure = closure.expect("[Benchmark] body did not call Bencher.iter()");
    
    // 3. Warmup
    for _ in 0..opts.warmup {
        interp.invoke_closure(&closure, &[]).ok();
    }
    
    // 4. Measure
    let mut samples = Vec::with_capacity(opts.samples);
    for _ in 0..opts.samples {
        let start = Instant::now();
        for _ in 0..opts.iters_per_sample {
            interp.invoke_closure(&closure, &[]).ok();
        }
        samples.push(start.elapsed().as_nanos() as f64 / opts.iters_per_sample as f64);
    }
    
    // 5. Statistics
    let median = quantile(&samples, 0.5);
    let lower  = quantile(&samples, 0.025);
    let upper  = quantile(&samples, 0.975);
    
    BenchResult {
        name:    test.function_name.clone(),
        median_ns: median,
        ci_lower:  lower,
        ci_upper:  upper,
        samples:   samples.len(),
    }
}
```

### Decision 5: TAP / JSON / Pretty 输出

完整继承原 P2 spec design.md Decision 5/6 的格式定义。略。

### Decision 6: Workspace 集成

[src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[workspace] members` 加 `../toolchain/test-runner`。

`cargo build -p z42-test-runner` 单独编译 runner（不影响 vm 主 build）。

### Decision 7: just 任务接入

替换 P0 / P2 阶段的占位：

```just
# 替换 P2 占位
test-changed:
    #!/usr/bin/env bash
    affected=$(./scripts/test-changed.sh)
    echo "$affected" | jq -e '.compiler' >/dev/null && just test-compiler
    echo "$affected" | jq -e '.vm_core'  >/dev/null && just test-vm
    for lib in $(echo "$affected" | jq -r '.stdlib[]?'); do
        just test-stdlib "$lib"
    done
    echo "$affected" | jq -e '.integration' >/dev/null && just test-integration

test-stdlib lib="":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ -z "{{lib}}" ]]; then
        for d in src/libraries/*/tests; do
            [[ -d "$d" ]] || continue
            cargo run -p z42-test-runner --release -- "$d"
        done
    else
        cargo run -p z42-test-runner --release -- "src/libraries/{{lib}}/tests/"
    fi

test-integration:
    cargo run -p z42-test-runner --release -- tests/integration/
```

### Decision 8: scripts/test-changed.sh

设计同原 P2 spec ([add-z42-test-runner/design.md Decision 8](../add-z42-test-runner/design.md))。直接复用，不重复说明。

## Implementation Notes

### Closure 提取协议

z42 closure 在 Rust 端表示为 `Value::Closure(...)`。runner 通过 thread-local 注册 callback：

- `Bencher.iter(closure)` 调用 native `__bench_set_iter_closure` 把 Value 存到 thread-local
- runner 跑完 [Benchmark] 函数后用 native `__bench_take_iter_closure` 取出
- 然后用 `Interpreter::invoke_closure(value)` 调用任意次

### method_id → 函数名映射

`module.functions[method_id]` 直接拿。

### 异常类型识别

用字符串包含匹配是简化方案；正确做法是解析 type registry。R4 加 typed expected_throw_type_idx 后，runner 用 type_idx 比对。

## Testing Strategy

- runner 集成测试：构造最小 .zbc + TestIndex → 跑 runner → 验证 outcome
- TAP/JSON formatter 单测
- Bencher 模式：构造空 closure → 验证 warmup + samples 工作
- 跨语言：编译 z42 测试程序 → runner 跑 → 退出码 + 输出符合预期
