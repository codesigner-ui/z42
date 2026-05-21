# Design: Parallel stage execution in test-all.sh

## Architecture

```
当前（after add-test-split-by-area）：
  for entry in "${STAGES[@]}"; do
      bash -c "$cmd"   # 串行
  done

本 spec 后（with --parallel）：
  STAGES grouped into waves (W1, W2, W3) per scope:
    W1: builds (parallel)
    W2: dotnet test + test-stdlib (parallel)
    W3: test-vm --no-rebuild + test-cross-zpkg (parallel)

  for wave in W1 W2 W3; do
      run_wave "${wave[@]}"
      [failure] → exit 1
  done

  run_wave() {
      for stage; do
          tmp=$(mktemp)
          bash -c "$cmd" > "$tmp" 2>&1 &
          pids+=($!); outs+=($tmp); names+=($name)
      done
      for p in "${pids[@]}"; do wait $p || fail=1; done
      for o in "${outs[@]}"; do cat $o; rm $o; done
      return $fail
  }
```

## Decisions

### Decision 1: Wave failure stops subsequent waves

**问题**：Wave 1 中某 stage 失败，Wave 2/3 还跑吗？

**决定**：**不跑**。理由：
- 后续 wave 假设 build artifacts 是新版本；失败的 build 意味着 artifact 旧/缺
- 串行模式本来就 fail-fast；保持行为一致
- 简化诊断（用户只看到首个失败的那一处）

Wave 内部并行 stage 失败：等同 wave 内 wait 后判断。**Wave 内其他 stage
继续跑完**（不互相 kill），尽量收集诊断信息。然后整 wave 报失败 + 退出。

### Decision 2: 失败时保留 temp 文件

**问题**：成功 wave 的 temp 文件该清，失败 wave 的留着 debug？

**决定**：**Wave 内任一 stage 失败 → 保留所有 temp 文件 + 输出路径**。
失败诊断需要原始输出。用户可用 `cat /tmp/<file>` 查看。Wave 内全成功 →
清干净。

实现：fail=1 时 skip cleanup；echo "stage outputs preserved at /tmp/…"
告诉用户位置。

### Decision 3: `--parallel` 不自动 piggyback 到 `auto`

**问题**：用户 `--scope=auto` 暗示"我想加速" → 自动开 `--parallel`？

**决定**：**不**。理由：
- `--scope=auto` 是 scope detection；`--parallel` 是执行策略。语义正交
- 加速选项 opt-in 才安全（--parallel 引入 race risk 即便我们小心；保留
  default off 让用户主动选择）
- 用户用 `--scope=auto --parallel` 显式组合明确

### Decision 4: Wave 安排基于依赖图

依赖图：
```
dotnet build ────► dotnet test
                ╲
                 ╲► test-vm
                 ╲► test-cross-zpkg
                 ╲► test-stdlib
cargo build    ───╱
                 ╱
test-stdlib    ──► test-vm (需要 stdlib zpkgs built)
                 ╲► test-cross-zpkg (同上)
```

3-wave 安排：
- W1: { dotnet build, cargo build } — 无 dep
- W2: { dotnet test, test-stdlib } — 都依赖 W1 builds；互相独立
- W3: { test-vm --no-rebuild, test-cross-zpkg } — 都依赖 W2 stdlib build

dotnet test 在 W2 而非 W3 因为它仅需 dotnet build（最早可跑），但放 W3
会增加 W3 串行成本，没好处。

### Decision 5: test-vm 强制 --no-rebuild

**问题**：parallel mode 下 test-vm 默认会 rebuild stdlib，与 W2 的
test-stdlib 时间窗口重合 → race。

**决定**：parallel mode 强制 `--no-rebuild`。test-vm 在 W3 跑时，W2 的
test-stdlib 已完成（含 stdlib build），artifacts 完整。test-vm 直接读现
有 zpkgs。

文档明记：parallel mode 假设 W2 已正确 build stdlib。如果 test-stdlib
build 失败但运行成功（unlikely），test-vm 在 W3 可能读到不完整 artifact。
v0 接受 — 任何 W2 部分失败立刻退出，W3 不会跑。

### Decision 6: 与 `--quick` 关系

**问题**：`--quick` 已经传 `--no-rebuild` 给 test-vm。`--parallel` 也是。
同时用？

**决定**：兼容。两个都设时 test-vm 一次 `--no-rebuild`（重复传 OK 因为
test-vm.sh 解析 flag 是 idempotent）。语义：parallel 一定 --no-rebuild；
quick 在 serial 模式也强制 --no-rebuild。

## Implementation Notes

### Main change to test-all.sh

```bash
PARALLEL=false
for arg in "$@"; do
    case "$arg" in
        --parallel) PARALLEL=true ;;
        # ... existing flags ...
    esac
done

# ... (scope detection + STAGES build remains) ...

if $PARALLEL && [ ${#STAGES[@]} -gt 0 ]; then
    run_parallel_waves
else
    run_sequential_stages
fi
```

### run_wave helper

```bash
# Runs each arg (stage entry "name|cmd") in parallel, captures output,
# prints serially after all wait. Returns 0 if all stages pass, 1 if any fail.
run_wave() {
    local pids=() outs=() names=() cmds=()
    for entry in "$@"; do
        IFS='|' read -r name cmd <<< "$entry"
        local out
        out=$(mktemp -t z42-test-all.XXXXXX)
        names+=("$name")
        cmds+=("$cmd")
        outs+=("$out")
        bash -c "$cmd" > "$out" 2>&1 &
        pids+=($!)
    done

    local fail=0
    local fail_idx=-1
    for i in "${!pids[@]}"; do
        if ! wait "${pids[$i]}"; then
            fail=1
            if [ $fail_idx -eq -1 ]; then fail_idx=$i; fi
        fi
    done

    # Print outputs in original stage order.
    for i in "${!outs[@]}"; do
        echo ""
        echo "════════════════════════════════════════════════"
        echo "  ${names[$i]}"
        echo "════════════════════════════════════════════════"
        cat "${outs[$i]}"
    done

    if [ $fail -eq 0 ]; then
        # Cleanup on success.
        for o in "${outs[@]}"; do rm -f "$o"; done
        return 0
    fi
    # Failure: preserve temp files for debugging.
    echo ""
    echo "wave failed at stage: ${names[$fail_idx]}"
    echo "stage outputs preserved at:"
    for i in "${!outs[@]}"; do
        echo "  ${names[$i]}: ${outs[$i]}"
    done
    return 1
}
```

### Wave dispatch per scope

```bash
# Force --no-rebuild on test-vm in parallel mode.
local STAGE_VM_PARALLEL="VM goldens|./scripts/test-vm.sh --no-rebuild"

case "$SCOPE" in
    full)
        run_wave "$STAGE_DOTNET_BUILD" "$STAGE_CARGO_BUILD" || exit 1
        run_wave "$STAGE_DOTNET_TEST" "$STAGE_STDLIB"       || exit 1
        run_wave "$STAGE_VM_PARALLEL" "$STAGE_CROSS_ZPKG"   || exit 1
        ;;
    runtime)
        run_wave "$STAGE_CARGO_BUILD"                       || exit 1
        run_wave "$STAGE_STDLIB"                            || exit 1
        run_wave "$STAGE_VM_PARALLEL" "$STAGE_CROSS_ZPKG"   || exit 1
        ;;
    compiler)
        run_wave "$STAGE_DOTNET_BUILD"                      || exit 1
        run_wave "$STAGE_DOTNET_TEST" "$STAGE_STDLIB"       || exit 1
        run_wave "$STAGE_VM_PARALLEL" "$STAGE_CROSS_ZPKG"   || exit 1
        ;;
    stdlib)
        run_wave "$STAGE_STDLIB"                            || exit 1
        run_wave "$STAGE_VM_PARALLEL" "$STAGE_CROSS_ZPKG"   || exit 1
        ;;
    docs-only)
        ;; # 0 stages, nothing to wave
esac
```

### Final summary line

After all waves, output: `✅ ALL GREEN (N waves, M stages, scope=X, parallel)`.

## Testing Strategy

- **Manual verification**:
  - `./scripts/test-all.sh --scope=stdlib --parallel` — 2 waves, all GREEN, prints stage outputs in order
  - `./scripts/test-all.sh --parallel` (scope=full) — 3 waves; compare total wall time vs serial
  - `--parallel` with deliberate build failure (touch syntax error in some src/runtime file) — verifies Wave 1 fail stops Wave 2/3, preserves temp files

## Deferred / Future Work

### `add-test-parallel-stage-splitting`
- Split test-stdlib into "build stdlib" + "run tests" sub-stages so
  dotnet test can run in W1 (alongside builds) and test-stdlib's run
  phase can join W3. Yields another ~30s savings. Requires test-stdlib.sh
  exposing build-only / run-only modes.

### `add-ci-parallel-test-all`
- Switch `.github/workflows/ci.yml` to invoke `test-all.sh --parallel`
  for faster CI runs.
