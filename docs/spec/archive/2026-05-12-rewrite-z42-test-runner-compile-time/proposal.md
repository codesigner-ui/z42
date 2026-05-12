# Proposal: z42-test-runner with Compile-Time Discovery

> 状态：🟡 已确认实施（2026-05-10 阶段 6.5 gate 通过）| 创建：2026-04-29 | 启动：2026-05-10
> 类型：refactor + extend（重构现有 test-runner，非 from-scratch）

## Why

[`add-z42-test-runner`](../../archive/2026-04-30-add-z42-test-runner/) (P2，已归档 ✅ 2026-04-30) 是 R3 minimal 版：subprocess fork z42vm，每个 [Test] 一个独立进程。当前 `src/toolchain/test-runner/src/main.rs` 是 770 行单文件实现。

R3 完整版要解决：
- **subprocess 启动开销** → in-process 执行（`interp::run` 已暴露公开 API）
- **[Setup] / [Teardown] hook 真生效** → R1 已把 Kind 写入 TIDX section；当前 runner 仅处理 [Test]
- **Bencher 调度模式** → R2 ✅ 2026-05-05 已交付 `Std.Test.Bencher` 类（custom ctor / iter / Min/Max/Median 等）；runner 缺 `--bench` 模式调度
- **zpkg-as-input** → 现 runner 仅吃 `.zbc`；zpkg 输入顺带覆盖跨非 import 包 inheritance（A3 chain 的边角）

R3 完整版**消费**：
- R1 的 `LoadedArtifact.test_index: Vec<TestEntry>`（编译时元数据，含 method_id / kind / flags / skip_reason / expected_throw_type）
- R2 的 z42.test 库（Assert / TestIO sink / Bencher）
- R4 (✅ 2026-04-30) attribute 校验 + A2/A3 (✅ 2026-04-30) [ShouldThrow<E>] inheritance chain（编译期 expand 进 TIDX，runner 仅 string 比对）

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
> **Scope 表已根据 2026-05-10 实施期状态修正**：原表把所有文件标 NEW（spec 起草于 2026-04-29，时存只占位）。当前 `test-runner` crate 已存在（770-line `main.rs` + `Cargo.toml` + `README.md`）。

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/test-runner/Cargo.toml` | MODIFY | 已存在；按需加 `regex` 依赖（filter 当前是 substring，spec 要 regex） |
| `src/toolchain/test-runner/src/main.rs` | MODIFY | 770 行 → 拆 ~80 行 CLI 入口 + 调度；其余迁移到模块 |
| `src/toolchain/test-runner/src/discover.rs` | NEW | 从 `main.rs::TestReport` 提取；按 design Decision 2 重写为支持 zpkg + 多 paths |
| `src/toolchain/test-runner/src/runner.rs` | NEW | in-process Setup → 主体 → Teardown 调度；替换原 `RunOutcome` subprocess fork |
| `src/toolchain/test-runner/src/bencher.rs` | NEW | `--bench` 模式 + closure stash/take 协议（Q4 决议） |
| `src/toolchain/test-runner/src/result.rs` | NEW | 从 `main.rs::TestResult/TestStatus` 提取 |
| `src/toolchain/test-runner/src/format/mod.rs` | NEW | Formatter trait |
| `src/toolchain/test-runner/src/format/tap.rs` | NEW | 从 `main.rs` 现有 TAP emitter 提取 |
| `src/toolchain/test-runner/src/format/json.rs` | NEW | 从 `main.rs` 现有 JSON emitter 提取 |
| `src/toolchain/test-runner/src/format/pretty.rs` | NEW | 从 `main.rs` 现有 pretty emitter 提取 |
| `src/toolchain/test-runner/tests/integration_test.rs` | NEW | runner 自身集成测试（端到端：编译一个 demo zbc → in-process 跑 → 断输出） |
| `src/toolchain/test-runner/README.md` | MODIFY | 已存在；更新 R3b 完整版能力描述 |
| `src/toolchain/README.md` | MODIFY | 列入 test-runner（如未列） |
| `src/runtime/Cargo.toml` | （免改动） | workspace members 已含 `../toolchain/test-runner` |
| `scripts/test-changed.sh` | （免改动） | R3c ✅ 2026-04-30 已落地 |
| `justfile` | （视情况）MODIFY | 若已替换占位则不动；按 R3 原 spec 检查 |
| `docs/design/test-runner.md` | NEW | runner 设计文档（in-process 架构 + Setup/Teardown 调度 + Bencher closure 协议） |
| `docs/design/testing/testing.md` | MODIFY | 新增 R3b in-process 段说明 |
| `docs/dev.md` | MODIFY | 加 "z42-test-runner" 段 |

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

## Open Questions（2026-05-10 阶段 6.5 已拍板）

- [x] **Q1**：每个 [Test] 一个独立 Interpreter 还是共享 + reset？ → **独立 VmContext per [Test]**（`static_fields_clear` 已有；隔离强；性能损失 ~ms 级，可接受）
- [x] **Q2**：Setup/Teardown 在 runner 侧调度还是包成 wrapper 函数？ → **runner 侧调度**（zero compiler change；同 VmContext 三调用 Setup → 主体 → Teardown 可共享 static state）
- [x] **Q3**：超时实现？ → **v0.1 不做**（推迟独立 spec；现有 runner 也无 `--timeout`）
- [x] **Q4**：Bencher 收集闭包的协议？ → **thread-local stash + take**：runner 在调用前 stash benchmark fn；R2 Bencher.Iter native 用 thread-local closure 反向调用宿主测时
- [x] **Q5**：CI 上 runner 路径？ → **dev 用 `cargo run`；CI 用 `cargo build --release -p z42-test-runner` 后跑 binary**（避免重复编译）
