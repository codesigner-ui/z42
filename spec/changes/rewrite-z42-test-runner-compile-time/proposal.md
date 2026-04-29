# Proposal: z42-test-runner with Compile-Time Discovery

## Why

[add-z42-test-runner](../add-z42-test-runner/) (P2) 的原方案在 runner 启动时扫整个 zbc method table 找带 attribute 的函数。R1 已把这一步前移到编译期：编译器写 `TestIndex` section，runner 直接读。

R3 实现新版 runner，**消费**：
- R1 的 `LoadedArtifact.test_index: Vec<TestEntry>`（编译时元数据）
- R2 的 z42.test 库（assertion / IO sink / Bencher）

不再扫 method table，runner 启动 O(1)。

## What Changes

### z42-test-runner 工具（src/toolchain/test-runner/）

参原 P2 spec 的 CLI 设计（[add-z42-test-runner/design.md](../add-z42-test-runner/design.md) Decision 4），但内部实现切换：

1. **Discovery**：`LoadedArtifact.test_index` 直接列出所有 entry —— 不需要扫 method table
2. **Execution**：对每个 TestEntry：
   - 解析 method_id → function 元数据
   - 应用 `--filter` / `--tag` / `--ignored` 过滤
   - 检查 flags：`Skipped` / `Ignored`
   - 执行：每个 [Test] 一个独立 Interpreter；调用 [Setup] → 测试体 → [Teardown]
   - 捕获结果：通过 R2 的 install_stdout_sink 默认捕获 stdout（仅失败时显示，参 Rust libtest）
   - 处理异常：catch TestFailure → status=failed；catch SkipSignal → status=skipped；catch 其他 → status=failed
   - 时间：bench 模式下用 `__bench_now_ns` 测时
3. **Output**：TAP 13 / JSON / pretty 三种 formatter（参原 P2 spec）
4. **Bencher 调度**（新）：[Benchmark] 函数有 Bencher 参数；runner 自动 warmup + iter，统计后输出
5. **TestCase 展开**：参数化测试每个 case 单独 entry；runner 反序列化 args 调用

### CLI 接口

完全继承原 P2 spec design.md Decision 4 的 CLI 设计，唯一新增 `--bench` 模式与 `--baseline` 选项。

### just 接入

替换 R3 之前 P2 占位的 `test-changed` / `test-stdlib` / `test-integration` 任务为完整实现（与原 P2 task spec 一致，路径和接口不变）。

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/test-runner/Cargo.toml` | NEW | crate manifest |
| `src/toolchain/test-runner/src/main.rs` | NEW | CLI 入口（clap） |
| `src/toolchain/test-runner/src/discover.rs` | NEW | 从 `LoadedArtifact.test_index` 收集 + 过滤 TestCase |
| `src/toolchain/test-runner/src/runner.rs` | NEW | 单 test 执行：sink 安装 / Setup / 主体 / Teardown / 捕获异常 / 时间 |
| `src/toolchain/test-runner/src/bencher.rs` | NEW | bench 模式：warmup + iter + 统计 + criterion-style 报告 |
| `src/toolchain/test-runner/src/result.rs` | NEW | TestStatus / TestResult / TestSuiteResult |
| `src/toolchain/test-runner/src/format/mod.rs` | NEW | Formatter trait |
| `src/toolchain/test-runner/src/format/tap.rs` | NEW | TAP 13 输出 |
| `src/toolchain/test-runner/src/format/json.rs` | NEW | JSON 输出 |
| `src/toolchain/test-runner/src/format/pretty.rs` | NEW | TTY 友好输出（colored） |
| `src/toolchain/test-runner/tests/integration_test.rs` | NEW | runner 自身集成测试 |
| `src/toolchain/test-runner/README.md` | NEW | 工具文档 |
| `src/toolchain/README.md` | MODIFY | 列入 test-runner |
| `src/runtime/Cargo.toml` | MODIFY | workspace members 加 `../toolchain/test-runner` |
| `scripts/test-changed.sh` | NEW | git diff → 受影响测试集（设计同原 P2） |
| `justfile` | MODIFY | `test-changed` / `test-stdlib` / `test-integration` 替换占位 |
| `docs/design/test-runner.md` | NEW | runner 设计文档（架构 + 调度流程） |

**只读引用**：
- [add-test-metadata-section/](../add-test-metadata-section/) (R1) — `LoadedArtifact.test_index` 字段契约
- [extend-z42-test-library/](../extend-z42-test-library/) (R2) — Assert / TestIO / Bencher API
- [add-z42-test-runner/](../add-z42-test-runner/) (P2 旧版，SUPERSEDED) — CLI 与 formatter 设计可借鉴
- [src/runtime/src/](src/runtime/src/) — Interpreter 公开 API

## Out of Scope

- **并行执行**（每线程独立 Interpreter）→ v0.2 / R6
- **doctest 提取与运行** → v0.2
- **snapshot review 工具** → v0.2
- **property-based 测试** → v0.3
- **测试覆盖率** → v1.0
- **自定义 harness 协议**（替代默认 runner）→ v0.2
- **attribute 签名校验** → R4
- **golden 用例迁移** → R5

## Open Questions

- [ ] **Q1**：每个 [Test] 一个独立 Interpreter 还是共享 + reset？
  - 倾向：独立 Interpreter（隔离强；性能损失 ~ms 级，可接受）
- [ ] **Q2**：Setup/Teardown 在 runner 侧调度还是包成 wrapper 函数？
  - 倾向：runner 侧调度（直观；无需修改用户代码）
- [ ] **Q3**：超时实现？(`--timeout` / `[Timeout]`)
  - 倾向：每个 Interpreter 跑独立 thread + signal/abort；v0.1 用 thread::join + try_join_timeout
- [ ] **Q4**：Bencher 收集闭包的协议？
  - 倾向：runner 注入特殊 marker；Bencher.iter 检测到 marker → 把 closure 引用通过 thread-local 传递回宿主
- [ ] **Q5**：CI 上 runner 路径？通过 `cargo run -p z42-test-runner` 还是预编译 binary？
  - 倾向：`cargo run` for dev；`cargo build --release -p z42-test-runner` 后用 binary（避免重复编译）
