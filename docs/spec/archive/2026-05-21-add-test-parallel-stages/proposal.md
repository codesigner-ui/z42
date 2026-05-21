# Proposal: Parallel stage execution in test-all.sh

## Why

`add-test-split-by-area` (2026-05-21) lets users skip stages that don't
apply to their change. Orthogonal optimization: even within the
remaining stages, several are mutually independent and could run in
parallel.

Stage dependency analysis:

| Stage | Depends on | Touches |
|-------|------------|---------|
| dotnet build | — | C# artifacts only |
| cargo build | — | Rust artifacts only |
| dotnet test | dotnet build | reads C# artifacts |
| test-stdlib | both builds | builds + reads stdlib zpkgs |
| test-vm (default) | both builds | rebuilds stdlib + runs |
| test-vm (`--no-rebuild`) | both builds + stdlib built | reads stdlib zpkgs |
| test-cross-zpkg | both builds | reads stdlib zpkgs; builds its own multi-zpkg |

Critical observation: `test-vm.sh` defaults to **rebuilding stdlib**
before running. If we run test-vm in parallel with test-stdlib, both
race on `artifacts/build/libs/`. Solution: in parallel mode, force
test-vm to `--no-rebuild` and order test-stdlib BEFORE test-vm so the
zpkgs are already built when test-vm starts.

Safe parallel waves:
- **Wave 1**: dotnet build || cargo build (independent toolchains)
- **Wave 2**: dotnet test || test-stdlib (dotnet test uses C# artifacts;
  test-stdlib uses Rust+stdlib — no overlap)
- **Wave 3**: test-vm --no-rebuild || test-cross-zpkg (both read pre-built
  stdlib from Wave 2; test-vm skips rebuild)

Expected speedup (scope=full, current times):
- Sequential: 30 + 40 + 10 + 60 + 30 + 90 ≈ 260s
- Parallel: max(30,40) + max(10,90) + max(20,30) ≈ 40 + 90 + 30 = **160s**
- Save ~100s (~38%)

Combined with `--scope=runtime`:
- Sequential 4 stages: 40 + 60 + 30 + 90 ≈ 220s
- Parallel: 40 (cargo) + 90 (stdlib) + 30 (vm+cross-zpkg) ≈ **160s**
  (only dotnet stages skipped from full)
- Save ~60s

## What Changes

- **New flag `--parallel`** — default off; opt-in. When set, runs each
  scope's stages in 3 waves as analyzed above. Builds + tests grouped
  by dependency edges (no race risk).
- **`--parallel` implies `--no-rebuild` on test-vm** — necessary to avoid
  the stdlib-rebuild race; documented as design decision.
- **Output capture** — each parallel stage's stdout/stderr → temp file;
  printed serially in wave-stage order after `wait`. Preserves the CI
  log readability + avoids interleaved noise.
- **Composition with `--scope=<value>`** — parallel waves adapt to the
  scope's stage set; stages absent in a scope are simply skipped from
  their wave.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/test-all.sh` | MODIFY | 加 `--parallel` 解析；`run_wave` helper 收集 stage 输出 → wait → 串行 print；各 scope 分 wave 调度 |
| `docs/workflow/testing/README.md` | MODIFY | Scope-aware 段下加 "Parallel waves" 子段；与 `--scope` 组合用法 |
| `docs/spec/changes/add-test-parallel-stages/` | NEW | 本 spec |

**只读引用**：

- `scripts/test-all.sh` 现 scope dispatch
- `scripts/test-vm.sh` `--no-rebuild` 标志
- `scripts/test-stdlib.sh` 重建路径
- `docs/spec/archive/2026-05-21-add-test-split-by-area/` proposal/design

## Out of Scope

- **CI 默认开启 `--parallel`**：CI 资源充裕，平均加速 ~38%。但本 spec 只
  加 flag；CI 是否开启另开 small spec `add-ci-parallel-test-all`
- **Stage splitting** — 把 test-stdlib 的 build 和 run 拆开能让 dotnet test
  在 Wave 2 与 stdlib build 并行 + test-stdlib run 进 Wave 3。但需要修
  test-stdlib.sh 暴露独立子命令；本 spec 不动 test-stdlib.sh
- **Interleaved real-time output** — 第三方工具 (parallel + tail -f) 风格
  实时多流。captured-print-serial 是简单可靠的 v0 方案
- **Process supervision / kill-on-fail** — wave 内某 stage failed 后是否
  立刻 kill 兄弟 stage？v0 等所有 wait 完成再判，最大化诊断信息

## Open Questions

- [ ] **Wave 内 fail 后续 wave 跳过**：第一个 wave 失败，后续 wave 不跑？
      Decision 1
- [ ] **temp 文件 cleanup**：失败路径要不要保留临时输出文件供 debug？
      Decision 2
- [ ] **是否给 `--scope=auto` 自动开 `--parallel`**：让 auto 模式默认快
      路径？Decision 3
