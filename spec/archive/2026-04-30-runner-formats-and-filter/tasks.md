# Tasks: Runner JSON/TAP Output + --filter (R3a)

> 状态：🟢 已完成 | 归档：2026-04-30 | 创建：2026-04-30
> 类型：feature；最小化模式
> 依赖：A2 + A3（runner ShouldThrow 已就绪）

## 变更说明

z42-test-runner 加 `--format <pretty|tap|json>` 输出与 `--filter <substring>` 筛选。pretty 是当前行为（默认），TAP 13 与 JSON 解锁 CI / 机器消费。

## 原因

- **CI 集成**：当前 runner 输出仅人类可读；GitHub Actions / Jenkins / etc. 需要机器格式 → JSON（自定义 schema）或 TAP 13（perl/Rust 生态主流）
- **大测试集筛选**：dogfood 只 8 个 test，但未来 stdlib `[Test]` 增长后跑全集变慢 → `--filter "should_throw"` 跑子集
- 完全 toolchain-side；不动 zbc / IR / VM

不做 in-process 执行 / Setup/Teardown / test-changed.sh（拆给 R3b、R3c）。

## 文档影响

- `docs/design/testing.md` 加 "Runner 输出格式" 段（pretty / TAP / JSON 三种 + JSON schema）
- `docs/roadmap.md` M6 R3a 标记完成；R3b/R3c 仍 backlog
- `src/toolchain/test-runner/README.md` 用法示例补 `--format` `--filter`

## 设计决策

### --filter：substring 而非 regex

简化：`test.method_name.contains(filter_str)`。不引入 regex crate依赖。需要更复杂模式时再升级。

### JSON schema

```jsonc
{
  "tool": "z42-test-runner",
  "version": "0.1.0",
  "module": "z42.test_dogfood.zbc",
  "summary": {
    "total": 8, "passed": 8, "failed": 0, "skipped": 0,
    "duration_ms": 173
  },
  "tests": [
    { "name": "Z42TestDogfood.test_assert_equal_pass",
      "status": "passed", "duration_ms": 142 },
    { "name": "Z42TestDogfood.test_skip_one",
      "status": "skipped", "reason": "platform=ios" },
    { "name": "Z42TestDogfood.test_fail_one",
      "status": "failed", "reason": "expected `Foo`, got `Bar`",
      "duration_ms": 3 }
  ]
}
```

固定字段；版本号用 crate version。schema 演化通过加字段（向后兼容）。

### TAP 13

```
TAP version 13
1..8
ok 1 - Z42TestDogfood.test_assert_equal_pass
ok 2 - Z42TestDogfood.test_assert_notequal_pass
not ok 3 - Z42TestDogfood.test_assert_fail_throws_testfailure
  ---
  message: 'expected to throw `TestFailure`, got `Std.SkipSignal`'
  ...
ok 4 - Z42TestDogfood.test_skip_one # SKIP platform=ios
```

参 [TAP 13 spec](https://testanything.org/tap-version-13-specification.html)。基础形态：`ok`/`not ok` + `# SKIP reason`；YAML block 仅 failed 时输出。

### 默认 format

- `--format` 未给：TTY → pretty；非 TTY → tap（CI-friendly）
- 显式 `--format pretty` / `--format tap` / `--format json` 强制
- pretty 与 TAP 都支持 `--no-color`（colored crate 已自动检测）

### 退出码（不变）

- `0` — 全部通过 / 仅有 skipped
- `1` — 任一 failed
- `2` — runner 内部错误（path 解析失败 etc.）
- `3` — 0 个 test discovered

## 实现思路

重构 `run`：先收集所有 `TestResult { name, status, duration_ms, reason }` 到 `Vec`，循环结束后调度到 formatter。三个 formatter 函数 + match Cli.format 派发。

新结构 `TestResult`：

```rust
struct TestResult {
    name: String,
    status: TestStatus,    // Passed / Failed / Skipped
    duration_ms: u64,
    reason: Option<String>,
}
```

将现有 `Outcome` enum 折叠成 `TestStatus`（Passed/Failed/Skipped + reason 字段）。

## 检查清单

- [x] 1.1 Cargo.toml 加 serde_json (already transitive via z42_vm but make explicit)
- [x] 1.2 Cli struct 加 `--format <pretty|tap|json>` (clap derive enum) + `--filter <SUBSTR>` (Option<String>)
- [x] 1.3 重构 `run`：把 outcome 收集到 `Vec<TestResult>`，单独函数 `run_all_tests` 返回 vec
- [x] 1.4 Format 派发：`match cli.format { Pretty => print_pretty(...), Tap => print_tap(...), Json => print_json(...) }`
- [x] 1.5 默认 format：检测 TTY (`std::io::IsTerminal` on stdout) → pretty 或 tap
- [x] 1.6 `--filter` 实现：在 `TestReport::from_artifact` 后过滤，按 `test.method_name.contains(filter)` 保留
- [x] 2.1 print_pretty：等价于现有输出（重构而非改写）
- [x] 2.2 print_tap：TAP 13 格式
- [x] 2.3 print_json：使用 serde_json，schema 如上
- [x] 3.1 Rust 单元测试：format 函数对合成 `Vec<TestResult>` 输出；snapshot-style 字符串断言
- [x] 3.2 `cargo test` 全绿
- [x] 4.1 `dotnet test` 816/816 不回归
- [x] 4.2 `./scripts/test-vm.sh` 208/208 不回归
- [x] 4.3 `./scripts/test-stdlib.sh` 8/0 dogfood 不回归（默认 format 仍为 pretty TTY）
- [x] 4.4 `./scripts/test-cross-zpkg.sh` 1/1 不回归
- [x] 4.5 手动验证：`z42-test-runner <zbc> --format json` / `--format tap` / `--filter equal` 输出符合 schema
- [x] 5.1 docs/design/testing.md 加输出格式段 + JSON schema
- [x] 5.2 src/toolchain/test-runner/README.md 加 `--format` `--filter` 用法
- [x] 5.3 docs/roadmap.md R3a 完成
- [x] 6.1 commit + push + 归档

## Out of scope（拆给后续 spec）

- ⏸️ in-process Interpreter 执行（R3b）
- ⏸️ Setup/Teardown 真生效（R3b 一并）
- ⏸️ test-changed.sh / git diff 反向图（R3c）
- ⏸️ Bencher 模式 / `--bench`（依赖 closure，R2.C/C 方向）
- ⏸️ `--tag` 过滤（z42 attribute 没有 tag 概念，需 spec 扩 [Test(tag: ...)]）
- ⏸️ regex filter（substring 够用；regex crate 等真有需求再加）

## 备注

- `colored` crate 已在 default-on TTY 模式下自动 disable 非 TTY；TAP/JSON formatter 不写颜色码（人类还是消费 pretty）
- TAP YAML 块只在 failed/skipped 输出 reason；passed 行只 `ok N - name` 一行
- JSON schema 设计为可扩——后续 R3b 加 `setup_duration_ms` / `teardown_duration_ms` 字段时不破坏现有消费者
- 所有 formatter 输出到 stdout；error 仍到 stderr（runner-internal errors）
