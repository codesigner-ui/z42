# Tasks: parallelize-and-jit-stdlib-tests

> 状态：🟡 进行中 | 创建：2026-06-21
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
- [ ] B.1 main.rs：`--mode interp|jit` clap value_enum flag（默认 interp，保持现状）
- [ ] B.2 bootstrap.rs：`Vm::new(mode)`；mode==jit 时 transitive eager-load BFS（port main.rs L566-630）
- [ ] B.3 subprocess 路径：fork z42vm 透传 `--mode <mode>`
- [ ] B.4 Rust 单测：bootstrap jit 加载 cross-zpkg dep 用例（cargo test）
- [ ] B.5 验证：`z42-test-runner <zbc> --mode jit` 在跨 zpkg 依赖测试上通过（z42.io/z42.crypto 等）

## 阶段 C: CI 接线
- [ ] C.1 ci.yml：linux-x64 跑 `xtask test stdlib --mode jit`（并入 vm-jit-consistency 或独立步骤）
- [ ] C.2 `xtask test stdlib` 增 `--mode` 透传（xtask_test.z42 + xtask_cli.z42）

## 阶段 D: 文档
- [ ] D.1 vm-architecture.md：test-runner exec-mode + bootstrap jit eager-load 原理
- [ ] D.2 docs/workflow/testing/README.md：stdlib 并行 + `--mode` 用法
- [ ] D.3 归档 + ACTIVE.md 释放

## 备注
- 并行安全性：dir-mode 合成 toml 名 `.xtask-<projName>.z42.toml` 已 per-unit 唯一，cache_dir per-unit，dist 文件名 per-unit → 并发编译不冲突（已确认）。
- jit 慢（每子进程 eager 编译闭包）→ stdlib-jit 只 linux-x64，且依赖阶段 A 并行才可承受。
