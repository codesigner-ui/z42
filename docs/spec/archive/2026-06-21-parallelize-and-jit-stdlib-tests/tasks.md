# Tasks: parallelize-and-jit-stdlib-tests

> 状态：🟢 已完成 | 创建：2026-06-21 | 完成：2026-06-21
> 子系统：`toolchain`（ACTIVE.md 登记）

## 进度概览
- [ ] 阶段 A: 并行化 stdlib [Test]（两阶段批量）
- [ ] 阶段 B: test-runner `--mode` 接口 + bootstrap jit eager-load
- [ ] 阶段 C: CI 接线 stdlib-jit linux-x64
- [ ] 阶段 D: 文档同步

## 阶段 A: 并行化 stdlib [Test]
- [x] A.0 compile 由 `dotnet run --no-build --project` → 直接 `dotnet z42c.dll`：3× 启动提速（0.78s→0.26s）、byte-identical、并发安全（无 obj/ race）。用户"简单改法"。全量 272/272 通过（193s 本地 baseline）
- [x] A.1 per-lib batch（复用 fixed `units[]`，无需动态列表）：`_testLibCore` 内 `foreach units` → `_runUnitsBatched`
- [x] A.2 两阶段批量（`_runUnitsBatched`）：spawn bsz 编译→wait（删 toml）→spawn bsz runner→wait+打印 TAP，仿 `_runVmBatch`。`_compileTestUnit` 重构为 `_compilePrep`（返回未 spawn Process）
- [x] A.3 输出聚合：compile error + TAP 用捕获的 `ProcessResult.Stdout`，batch 顺序打印（Process 默认 Pipe）
- [x] A.4 `jobs` 语义改为 unit 级 batch 宽度（runner 不再收 --jobs → Setup/Teardown 恢复执行）；`test all` 已传 4
- [x] A.5 验证：全量 272/272 通过；serial 193s → jobs=4 96s（**2× 本地**）；jobs=1 默认路径亦过

## 阶段 B: test-runner --mode 接口
- [x] B.1 main.rs：`--mode interp|jit` clap value_enum flag（默认 interp）
- [x] B.2 **设计修正**：in-process runner（runner.rs）硬编码 `interp::run_outcome`，从不读 `loaded.vm` → 无法 in-process jit。改为 **`--mode jit` 强制 subprocess**（fork z42vm --mode jit，复用 z42vm 已有 transitive eager-load + jit）。bootstrap.rs 还原 interp-only（其 eager-load 改动撤销）
- [x] B.3 subprocess 路径（exec.rs/parallel.rs）：fork z42vm 透传 `--mode <mode>`
- [~] B.4 Rust 单测：run_one 签名变更，现有 skip_eval 单测不受影响；cargo test 绿（新 jit-load 用例略——逻辑复用 z42vm 已测路径）
- [x] B.5 验证：`z42-test-runner <zbc> --mode jit` 通过；全量 stdlib --mode jit **272/272**（16min 本地）

## 阶段 C: CI 接线
- [x] C.2 `xtask test stdlib --mode` 透传（xtask_test.z42 `_testLibCore`/`_runUnitsBatched` + xtask_cli.z42 stdlib/bench leaf option）；`test all` 显式传 "interp"
- [x] C.1 ci.yml：新增**独立并行 job** `stdlib-jit-consistency (linux-x64)`（User 选 placement）跑 `test stdlib --mode jit --jobs 4`；不进 publish-nightly needs（download-bootstrap 死锁规避）

## 阶段 D: 文档
- [x] D.1 `docs/design/testing/testing.md`：exec-mode 设计段（in-process/subprocess × interp/jit 正交但耦合；为何不做 in-process jit；xtask --jobs ≠ runner --jobs）。**改投 testing.md 而非 proposal 里写的 vm-architecture.md**——这是 test-tooling 机制非 VM 核心
- [x] D.2 `docs/workflow/testing/stdlib-tests.md`：`--jobs` 并行 + `--mode jit` 用法 + 机制；顺带修 stale `test lib`→`test stdlib`、错误的 `--format` 示例
- [x] D.3 归档 + ACTIVE.md 释放

## 备注
- 并行安全性：dir-mode 合成 toml 名 `.xtask-<projName>.z42.toml` 已 per-unit 唯一，cache_dir per-unit，dist 文件名 per-unit → 并发编译不冲突（已确认）。
- jit 慢（每子进程 eager 编译闭包）→ stdlib-jit 只 linux-x64，且依赖阶段 A 并行才可承受。
