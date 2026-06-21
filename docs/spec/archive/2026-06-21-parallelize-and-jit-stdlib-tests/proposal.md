# Proposal: parallelize-and-jit-stdlib-tests

## Why

CI `xtask test all` 在慢 runner 上 ~43min，两个大头：
- **stdlib [Test] 21.8min**：272 个 unit **完全串行** compile（每个 `dotnet run` 一次启动 ~1.5s）+ run。
- **VM goldens jit ~15min**：每 golden 子进程重 jit 编译整个闭包；且与 vm-jit-consistency 冗余（独立议题，本 change 不动）。

同时存在**真覆盖缺口**：stdlib [Test] 只跑 interp（test-runner 内嵌 VM 写死 `ExecMode::Interp`，[bootstrap.rs:112](../../../src/toolchain/test-runner/src/bootstrap.rs)），无 jit 校验。

直接给串行 stdlib 加 jit 会让该 stage 从 21.8min 暴涨到数小时。**故先并行化（前置），再加 jit 接口。**

## What Changes

1. **并行化 stdlib [Test]**（lever A）：272 unit 的 compile+run 由串行改两阶段批量（Spawn/Wait，仿 `_runVmBatch`）。21.8min → ~6min，零覆盖损失。
2. **test-runner `--mode interp|jit` 接口**：in-process runner 支持选执行模式；jit 模式把 main.rs 的 transitive eager-load BFS port 进 bootstrap.rs（jit 需全闭包预加载）。subprocess 路径透传 `--mode`。
3. **stdlib-jit 只挂 linux-x64**：新增/扩展一个 linux-x64 CI 步骤跑 stdlib [Test] jit（仿 vm-jit-consistency），不进全平台 `test all`（成本控制）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/xtask_test.z42` | MODIFY | `_testLibCore` 串行 unit 循环 → 两阶段批量并行；新增 `--jobs` 透传到 unit 级 |
| `src/toolchain/test-runner/src/main.rs` | MODIFY | 加 `--mode interp\|jit` clap flag；threaded 到 in-process + subprocess 路径 |
| `src/toolchain/test-runner/src/bootstrap.rs` | MODIFY | `Vm::new(mode)`；jit 模式 transitive eager-load（port main.rs BFS） |
| `.github/workflows/ci.yml` | MODIFY | stdlib-jit linux-x64 步骤（vm-jit-consistency job 内追加 或 新步骤） |
| `scripts/xtask_cli.z42` | MODIFY | （如需）`test stdlib` 增 `--mode` flag 声明 |
| `docs/design/runtime/vm-architecture.md` 或 compiler/test 文档 | MODIFY | test-runner exec-mode 接口 + bootstrap jit eager-load 原理 |
| `docs/workflow/testing/README.md` | MODIFY | stdlib 并行 + `--mode` 用法 |

**只读引用**：
- `scripts/xtask_test_vm.z42` — `_runVmBatch` 并行模板
- `src/runtime/src/main.rs` — eager-load BFS 源（L566-630）
- `src/libraries/z42.io/src/ProcessHandle.z42` — Spawn/Wait API

## Out of Scope
- VM goldens jit 去冗余（lever B）：独立 change，需 User 决策 per-platform jit 取舍
- z42c-as-test-compiler（用 native z42c 替 dotnet driver 编译测试）：另一条加速线，behavior 风险，单独评估
- 把 stdlib-jit 上全平台 `test all`

## Open Questions
- [ ] stdlib-jit 在 linux-x64 是并入 vm-jit-consistency job 还是独立 job？（实施时定，倾向并入）
