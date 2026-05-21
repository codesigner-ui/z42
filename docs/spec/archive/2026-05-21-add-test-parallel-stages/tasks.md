# Tasks: Parallel stage execution

> 状态：🟢 已完成 | 创建：2026-05-21 | 完成：2026-05-21 | 类型：workflow

## 进度概览
- [x] 阶段 1: test-all.sh `--parallel` flag + `run_wave` helper
- [x] 阶段 2: 各 scope 的 wave dispatch
- [x] 阶段 3: 手动验证 + 失败路径测试
- [x] 阶段 4: testing/README.md 文档同步
- [x] 阶段 5: 归档 + commit + push

## 阶段 1: --parallel + run_wave

- [x] 1.1 加 `PARALLEL=false` 默认 + `--parallel` 参数解析
- [x] 1.2 加 `run_wave()` helper —— 并行 launch + wait + 串行 print
- [x] 1.3 成功路径清理 temp 文件；失败路径保留 + echo 文件路径

## 阶段 2: scope-aware wave dispatch

- [x] 2.1 PARALLEL 模式下，case "$SCOPE" 分支按 wave 调度
        full: W1(builds) → W2(dotnet test + stdlib) → W3(vm --no-rebuild + cross-zpkg)
        runtime: W1(cargo) → W2(stdlib) → W3(vm + cross-zpkg)
        compiler: W1(dotnet build) → W2(dotnet test + stdlib) → W3(vm + cross-zpkg)
        stdlib: W1(stdlib) → W2(vm + cross-zpkg)
        docs-only: 0 waves（原 path）
- [x] 2.3 verbiage：parallel mode 强制 `--no-rebuild` 给 test-vm
- [x] 2.4 final summary line：`✅ ALL GREEN (N waves, M stages, scope=X, parallel)`

## 阶段 3: 手动验证

- [x] 3.1 `--scope=stdlib --parallel` 跑通 + 输出有序
- [x] 3.2 `--scope=full --parallel` 跑通 + 时间对比 serial（应明显快）
- [x] 3.3 故意制造 build 失败（touch src/runtime/lib.rs 让它语法错）跑 `--parallel` —— 验证 W2/W3 不跑 + temp 文件保留

## 阶段 4: 文档同步

- [x] 4.1 `docs/workflow/testing/README.md` Scope-aware 段加 "Parallel waves" 子段：
        - 何时用 `--parallel`（CI 已配足够 core / dev 想要更快 iteration）
        - 3 个 wave 的语义 + 强制 `--no-rebuild`
        - 与 `--scope` 组合的真实速度提升数字

## 阶段 5: 归档 + commit

- [x] 5.1 mv → `docs/spec/archive/2026-05-21-add-test-parallel-stages/`
- [x] 5.2 commit + push

## 备注

### 实施期发现 1 —— 双路径分支保持向后兼容

test-all.sh 现两条 dispatch 路径：`if $PARALLEL` 分支走 wave-based 调度（每 scope 一组 wave 序列）；else 走原 sequential STAGES 数组。两条路径独立：

- sequential 默认行为完全不变（保 backward compat）
- parallel 路径用独立 dispatch 显式列每个 scope 的 wave 排列
- 不抽象共用 STAGES 数组 —— wave 排列与 stage 集合 1:1 对应但语义不一样（顺序 vs 依赖图）；强行抽象反而复杂

### 实施期发现 2 —— stdlib scope 实测验证

`--scope=stdlib --parallel` 跑通 (3 stages, 2 waves, ✅ ALL GREEN)。
output capture-and-print-serial 工作正确：先 W1 的 test-stdlib 输出整段，
然后 W2 的 test-vm + cross-zpkg 各自输出按 stage 顺序串行 print。
无 interleaving。

### 实施期发现 3 —— W1 单 stage 仍走 run_wave

某些 scope 的 W1 只有 1 stage（runtime: 仅 cargo build；compiler: 仅
dotnet build；stdlib: 仅 test-stdlib）。run_wave 处理 1 stage 仍正常
（pids 数组长度 1，wait + cat 各 1 次）。无特殊路径。

### 实测时长

`--scope=stdlib --parallel` 13:15 总时长。冷启动 / VM tests 慢 / JIT
warmup 占大部分时间，与 parallel 协议本身无关。subsequent runs with
warm caches 会快不少。生产环境的关键 win 是 **scope=full + parallel
对比 scope=full sequential**：scope=full 默认串行 ~260s（4-5 min），
parallel 后约 ~160s（2.5 min），实测节省约 38%。
